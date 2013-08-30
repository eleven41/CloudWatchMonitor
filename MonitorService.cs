using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceProcess;
using System.Threading;
using Amazon;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.EC2;
using Amazon.EC2.Model;
using Microsoft.VisualBasic.Devices;

namespace CloudWatchMonitor
{
    public partial class MonitorService : ServiceBase
    {
        // Constants
        private const string CloudWatchNamespace = "System/Windows";

        // How often to send stats to Cloud Watch
        // Will be read from .config file
        private int _monitorPeriodInMinutes;

        // The event upon which we wait for our signal to stop
        // the service.
        private readonly ManualResetEvent _evStop = new ManualResetEvent(false);

        // Amazon Access Key and Secret Access Key.
        // Will be read from .config file
        private string _amazonAccessKeyId;
        private string _amazonSecretAccessKey;

        // Amazon Simple Notification Service Topics for alarms.
        // Will be read from .config file
        private List<string> _amazonSnsTopics;
        private EventLog _eventLog;

        // Instance ID of the current running instance.
        // Will be populated by communicating with metadata server.
        private string _instanceId;

        // 'Name' tag value of the current running instance.
        // Will be populated by communicating with EC2 API.
        private string _instanceName;

        // Region of the current running instance.
        // Will be populated by communicating with the metadata server.
        private string _region;

        // Disk metrics
        private bool _isSubmitDiskSpaceAvailable;
        private bool _isSubmitDiskSpaceUsed;
        private bool _isSubmitDiskSpaceUtilization;

        // Drives to to explicitly included
        // Will be read from .config file
        private List<string> _includeDrives;

        // Memory metrics
        private bool _isSubmitMemoryAvailable;
        private bool _isSubmitMemoryUsed;
        private bool _isSubmitMemoryUtilization;
        private bool _isSubmitPhysicalMemoryAvailable;
        private bool _isSubmitPhysicalMemoryUsed;
        private bool _isSubmitPhysicalMemoryUtilization;
        private bool _isSubmitVirtualMemoryAvailable;
        private bool _isSubmitVirtualMemoryUsed;
        private bool _isSubmitVirtualMemoryUtilization;

        public MonitorService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            // Since we are running as a service, setup an event log
            const string eventLogSource = "CloudWatchMonitor";
            if (!EventLog.SourceExists(eventLogSource))
            {
                // Requires to be administrator for this to succeed
                EventLog.CreateEventSource(eventLogSource, "Eleven41");
            }
            _eventLog = new EventLog {Source = eventLogSource};

            // Start our main worker thread
            new Thread(Run).Start();

            // When we leave here, our service will be running

            // Why use a worker thread and not a timer?
            // Original implementation had a timer, but the timer 
            // seemed to mysteriously stop ticking after about 15 minutes.
            // No solution was found, so I reverted back to a worker thread.
        }

        protected override void OnStop()
        {
            // Signal the worker thread to stop
            _evStop.Set();
        }

        private void Info(string message, params Object[] args)
        {
            // If we are running in service mode, then
            // ignore the informational message, otherwise
            // forward to the console.
            if (_eventLog == null)
            {
                Console.WriteLine(message, args);
            }
        }

        private void Error(string message, params Object[] args)
        {
            // If we are running in service mode, then
            // send the message to the event log, otherwise
            // forward to the console.
            if (_eventLog == null)
            {
                Console.WriteLine("E:" + message, args);
            }
            else
            {
                string finalMessage = String.Format(message, args);
                _eventLog.WriteEntry(finalMessage, EventLogEntryType.Error);
            }
        }

        private bool ReadBoolean(string name, bool defaultValue)
        {
            string temp = ConfigurationManager.AppSettings[name];
            if (String.IsNullOrEmpty(temp))
                return defaultValue;


            bool result;
            if (!Boolean.TryParse(temp, out result))
                throw new Exception(String.Format("{0} must be True or False: {1}", name, temp));

            return result;
        }

        private int ReadInt(string name, int defaultValue)
        {
            string temp = ConfigurationManager.AppSettings[name];
            if (String.IsNullOrEmpty(temp))
                return defaultValue;

            int result;
            if (!Int32.TryParse(temp, out result))
                throw new Exception(String.Format("{0} must be a number: {1}", name, temp));

            return result;
        }

        private string ReadString(string name, string defaultValue)
        {
            string temp = ConfigurationManager.AppSettings[name];
            if (String.IsNullOrEmpty(temp))
                return defaultValue;
            return temp;
        }

        private List<string> ReadStringList(string name, List<string> defaultValue)
        {
            string temp = ConfigurationManager.AppSettings[name];
            if (String.IsNullOrEmpty(temp))
                return defaultValue;

            string[] values = temp.Split(',');
            return values.Select(s => s.Trim()).Where(s => !String.IsNullOrEmpty(s)).ToList();
        }

        // Primary running loop for the service
        public void Run()
        {
            Info("CloudWatch Monitor starting");

            ReadConfiguration();

            while (true)
            {
                DateTime updateBegin = DateTime.Now;

                try
                {
                    UpdateMetrics();
                }
                catch (Exception e)
                {
                    Error("Error submitting metrics: {0}", e.Message);

                    // Ignore the error and continue
                }

                DateTime updateEnd = DateTime.Now;
                TimeSpan updateDiff = (updateEnd - updateBegin);

                TimeSpan baseTimeSpan = TimeSpan.FromMinutes(_monitorPeriodInMinutes);

                TimeSpan timeToWait = (baseTimeSpan - updateDiff);
                if (timeToWait.TotalMilliseconds < 50)
                    timeToWait = TimeSpan.FromMilliseconds(50);

                // This event gives us our pause along with
                // our signal to shut down.  If the signal 
                // arrives, then we shutdown, otherwise
                // we've just paused the desired amount.
                if (_evStop.WaitOne(timeToWait, false))
                    break;
            }

            Info("CloudWatch Monitor shutting down");
        }

        private void ReadConfiguration()
        {
            try
            {
                Info("Reading configuration");

                _monitorPeriodInMinutes = ReadInt("MonitorPeriodInMinutes", 1);

                // Validate min/max values
                if (_monitorPeriodInMinutes < 1)
                    throw new Exception("MonitorPeriodInMinutes must be greater than or equal to 1");
                Info("MonitorPeriodInMinutes: {0}", _monitorPeriodInMinutes);

                _isSubmitDiskSpaceAvailable = ReadBoolean("SubmitDiskSpaceAvailable", true);
                Info("SubmitDiskSpaceAvailable: {0}", _isSubmitDiskSpaceAvailable);

                _isSubmitDiskSpaceUsed = ReadBoolean("SubmitDiskSpaceUsed", true);
                Info("SubmitDiskSpaceUsed: {0}", _isSubmitDiskSpaceUsed);

                _isSubmitDiskSpaceUtilization = ReadBoolean("SubmitDiskSpaceUtilization", true);
                Info("SubmitDiskSpaceUtilization: {0}", _isSubmitDiskSpaceUtilization);

                _isSubmitMemoryAvailable = ReadBoolean("SubmitMemoryAvailable", true);
                Info("SubmitMemoryAvailable: {0}", _isSubmitMemoryAvailable);

                _isSubmitMemoryUsed = ReadBoolean("SubmitMemoryUsed", true);
                Info("SubmitMemoryUsed: {0}", _isSubmitMemoryUsed);

                _isSubmitMemoryUtilization = ReadBoolean("SubmitMemoryUtilization", true);
                Info("SubmitMemoryUtilization: {0}", _isSubmitMemoryUtilization);

                _isSubmitPhysicalMemoryAvailable = ReadBoolean("SubmitPhysicalMemoryAvailable", true);
                Info("SubmitPhysicalMemoryAvailable: {0}", _isSubmitPhysicalMemoryAvailable);

                _isSubmitPhysicalMemoryUsed = ReadBoolean("SubmitPhysicalMemoryUsed", true);
                Info("SubmitPhysicalMemoryUsed: {0}", _isSubmitPhysicalMemoryUsed);

                _isSubmitPhysicalMemoryUtilization = ReadBoolean("SubmitPhysicalMemoryUtilization", true);
                Info("SubmitPhysicalMemoryUtilization: {0}", _isSubmitPhysicalMemoryUtilization);

                _isSubmitVirtualMemoryAvailable = ReadBoolean("SubmitVirtualMemoryAvailable", true);
                Info("SubmitVirtualMemoryAvailable: {0}", _isSubmitVirtualMemoryAvailable);

                _isSubmitVirtualMemoryUsed = ReadBoolean("SubmitVirtualMemoryUsed", true);
                Info("SubmitVirtualMemoryUsed: {0}", _isSubmitVirtualMemoryUsed);

                _isSubmitVirtualMemoryUtilization = ReadBoolean("SubmitVirtualMemoryUtilization", true);
                Info("SubmitVirtualMemoryUtilization: {0}", _isSubmitVirtualMemoryUtilization);

                _includeDrives = ReadStringList("IncludeDrives", null);
                if (_includeDrives != null)
                    Info("IncludeDrives: {0}", String.Join(",", _includeDrives));
                else
                    Info("IncludeDrives: All drives");

                _instanceId = ReadString("InstanceId", null);
                if (!String.IsNullOrEmpty(_instanceId))
                    Info("Instance ID: {0}", _instanceId);

                _region = ReadString("Region", null);
                if (!String.IsNullOrEmpty(_region))
                    Info("Region: {0}", _region);
            }
            catch (Exception e)
            {
                Error(e.Message);
                if (!Environment.UserInteractive)
                    Stop(); // Tell the service to stop
                return;
            }

            if (!_isSubmitDiskSpaceAvailable &&
                !_isSubmitDiskSpaceUsed &&
                !_isSubmitDiskSpaceUtilization &&
                !_isSubmitMemoryAvailable &&
                !_isSubmitMemoryUsed &&
                !_isSubmitMemoryUtilization &&
                !_isSubmitPhysicalMemoryAvailable &&
                !_isSubmitPhysicalMemoryUsed &&
                !_isSubmitPhysicalMemoryUtilization &&
                !_isSubmitVirtualMemoryAvailable &&
                !_isSubmitVirtualMemoryUsed &&
                !_isSubmitVirtualMemoryUtilization)
            {
                Error("No data is selected to submit.");
                if (!Environment.UserInteractive)
                    Stop(); // Tell the service to stop
                return;
            }

            // Read the Amazon access key information from the config file.
            // Amazon will validate this later.
            _amazonAccessKeyId = ConfigurationManager.AppSettings["AWSAccessKey"];
            _amazonSecretAccessKey = ConfigurationManager.AppSettings["AWSSecretKey"];

            // Read the Amazon SNS topics from the config file.
            // Amazon will validate this later.
            char[] separatorCharacters = {','};
            _amazonSnsTopics =
                ConfigurationManager.AppSettings["AlarmSNSTopics"].Split(separatorCharacters,
                    StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        public void CreateAlarms()
        {
            ReadConfiguration();

            if (!PopulateInstanceId())
                return;

            if (!PopulateInstanceName())
                return;

            if (_isSubmitDiskSpaceUtilization)
            {
                // Get the list of drives from the system
                DriveInfo[] drives = DriveInfo.GetDrives();
                foreach (DriveInfo drive in drives)
                {
                    string driveName = String.Format("{0}", drive.Name[0]);

                    // If we need to filter drives, then only include those
                    // explicitly specified.
                    if (_includeDrives != null)
                    {
                        if (!_includeDrives.Contains(driveName))
                        {
                            Info("Not including drive: {0}", driveName);
                            continue;
                        }
                    }

                    CreateDriveUtilizationAlarm(driveName);
                }
            }

            if (_isSubmitPhysicalMemoryUtilization)
            {
                CreatePhysicalMemoryUtlizationAlarm();
            }

            CreateCpuUtilizationAlarm();

            CreateStatusCheckAlarm();
        }

        private void CreateDriveUtilizationAlarm(string driveName)
        {
            var dimensions = new List<Dimension>
            {
                new Dimension
                {
                    Name = "InstanceId",
                    Value = _instanceId
                },
                new Dimension
                {
                    Name = "Drive",
                    Value = driveName
                }
            };

            var putMetricAlarmReq = new PutMetricAlarmRequest
            {
                ActionsEnabled = true,
                AlarmActions = _amazonSnsTopics,
                AlarmDescription = "Disk space utilization alarm",
                AlarmName = _instanceName + "-disk-space-" + driveName,
                ComparisonOperator = "GreaterThanOrEqualToThreshold",
                Dimensions = dimensions,
                EvaluationPeriods = 1,
                MetricName = "DiskSpaceUtilization",
                Namespace = "System/Windows",
                Period = 60*5,
                Statistic = "Average",
                Threshold = 85,
                Unit = "Percent"
            };

            RegionEndpoint regionEndpoint = RegionEndpoint.GetBySystemName(_region);
            AmazonCloudWatch cloudwatchClient = AWSClientFactory.CreateAmazonCloudWatchClient(regionEndpoint);

            cloudwatchClient.PutMetricAlarm(putMetricAlarmReq);
        }

        private void CreatePhysicalMemoryUtlizationAlarm()
        {
            var dimensions = new List<Dimension>
            {
                new Dimension()
                    .WithName("InstanceId")
                    .WithValue(_instanceId)
            };

            var putMetricAlarmReq = new PutMetricAlarmRequest
            {
                ActionsEnabled = true,
                AlarmActions = _amazonSnsTopics,
                AlarmDescription = "Physical memory utilization alarm",
                AlarmName = _instanceName + "-physical-memory",
                ComparisonOperator = "GreaterThanOrEqualToThreshold",
                Dimensions = dimensions,
                EvaluationPeriods = 1,
                MetricName = "PhysicalMemoryUtilization",
                Namespace = "System/Windows",
                Period = 60*5,
                Statistic = "Average",
                Threshold = 85,
                Unit = "Percent"
            };

            RegionEndpoint regionEndpoint = RegionEndpoint.GetBySystemName(_region);
            AmazonCloudWatch cloudwatchClient = AWSClientFactory.CreateAmazonCloudWatchClient(regionEndpoint);

            cloudwatchClient.PutMetricAlarm(putMetricAlarmReq);
        }

        private void CreateCpuUtilizationAlarm()
        {
            var dimensions = new List<Dimension>
            {
                new Dimension
                {
                    Name = "InstanceId",
                    Value = _instanceId
                }
            };

            var putMetricAlarmReq = new PutMetricAlarmRequest
            {
                ActionsEnabled = true,
                AlarmActions = _amazonSnsTopics,
                AlarmDescription = "CPU utilization alarm",
                AlarmName = _instanceName + "-CPU-utilization",
                ComparisonOperator = "GreaterThanOrEqualToThreshold",
                Dimensions = dimensions,
                EvaluationPeriods = 1,
                MetricName = "CPUUtilization",
                Namespace = "AWS/EC2",
                Period = 60*5,
                Statistic = "Average",
                Threshold = 85,
                Unit = "Percent"
            };

            RegionEndpoint regionEndpoint = RegionEndpoint.GetBySystemName(_region);
            AmazonCloudWatch cloudwatchClient = AWSClientFactory.CreateAmazonCloudWatchClient(regionEndpoint);

            cloudwatchClient.PutMetricAlarm(putMetricAlarmReq);
        }

        private void CreateStatusCheckAlarm()
        {
            var dimensions = new List<Dimension>
            {
                new Dimension()
                    .WithName("InstanceId")
                    .WithValue(_instanceId)
            };

            var putMetricAlarmReq = new PutMetricAlarmRequest
            {
                ActionsEnabled = true,
                AlarmActions = _amazonSnsTopics,
                AlarmDescription = "Status check alarm",
                AlarmName = _instanceName + "-status-check",
                ComparisonOperator = "GreaterThanOrEqualToThreshold",
                Dimensions = dimensions,
                EvaluationPeriods = 1,
                MetricName = "StatusCheckFailed",
                Namespace = "AWS/EC2",
                Period = 60*5,
                Statistic = "Average",
                Threshold = 1,
                Unit = "Count"
            };

            RegionEndpoint regionEndpoint = RegionEndpoint.GetBySystemName(_region);
            AmazonCloudWatch cloudwatchClient = AWSClientFactory.CreateAmazonCloudWatchClient(regionEndpoint);

            cloudwatchClient.PutMetricAlarm(putMetricAlarmReq);
        }

        private void UpdateMetrics()
        {
            if (!PopulateInstanceId())
                return;

            if (!PopulateRegion())
                return;

            var metrics = new List<MetricDatum>();

            if (_isSubmitDiskSpaceAvailable ||
                _isSubmitDiskSpaceUsed ||
                _isSubmitDiskSpaceUtilization)
            {
                // Get the list of drives from the system
                DriveInfo[] drives = DriveInfo.GetDrives();
                foreach (DriveInfo drive in drives)
                {
                    AddDriveMetrics(drive, metrics);
                }
            }

            if (_isSubmitMemoryAvailable ||
                _isSubmitMemoryUsed ||
                _isSubmitMemoryUtilization ||
                _isSubmitPhysicalMemoryAvailable ||
                _isSubmitPhysicalMemoryUsed ||
                _isSubmitPhysicalMemoryUtilization ||
                _isSubmitVirtualMemoryAvailable ||
                _isSubmitVirtualMemoryUsed ||
                _isSubmitVirtualMemoryUtilization)
            {
                SubmitMemoryMetrics(metrics);
            }

            Info("\tSubmitting metric data");

            for (int skip = 0;; skip += 20)
            {
                IEnumerable<MetricDatum> metricsThisRound = metrics.Skip(skip).Take(20);
                if (metricsThisRound.Count() == 0)
                    break;

                PutMetricDataRequest request = new PutMetricDataRequest()
                    .WithNamespace(CloudWatchNamespace)
                    .WithMetricData(metricsThisRound);

                AmazonCloudWatch client = CreateClient();
                PutMetricDataResponse response = client.PutMetricData(request);
            }

            // We don't care about the response

            Info("Done.");
        }

        private AmazonCloudWatch CreateClient()
        {
            // Submit in the region that the instance is local to
            var config = new AmazonCloudWatchConfig
            {
                ServiceURL = String.Format("http://monitoring.{0}.amazonaws.com", _region)
            };
            return new AmazonCloudWatchClient(_amazonAccessKeyId, _amazonSecretAccessKey, config);
        }

        private void SubmitMemoryMetrics(List<MetricDatum> metrics)
        {
            Info("Adding memory metrics");

            var dimensions = new List<Dimension>();
            dimensions.Add(new Dimension()
                .WithName("InstanceId")
                .WithValue(_instanceId));

            // Why is this in a visual basic namespace?
            var computerInfo = new ComputerInfo();

            double availablePhysicalMemory = computerInfo.AvailablePhysicalMemory;
            double totalPhysicalMemory = computerInfo.TotalPhysicalMemory;
            double physicalMemoryUsed = (totalPhysicalMemory - availablePhysicalMemory);
            double physicalMemoryUtilized = (physicalMemoryUsed/totalPhysicalMemory)*100;

            Info("\tTotal Physical Memory: {0:N0} bytes", totalPhysicalMemory);

            if (_isSubmitPhysicalMemoryUsed)
            {
                Info("\tPhysical Memory Used: {0:N0} bytes", physicalMemoryUsed);
                metrics.Add(new MetricDatum()
                    .WithMetricName("PhysicalMemoryUsed")
                    .WithUnit("Bytes")
                    .WithValue(physicalMemoryUsed)
                    .WithDimensions(dimensions));
            }

            if (_isSubmitPhysicalMemoryAvailable)
            {
                Info("\tAvailable Physical Memory: {0:N0} bytes", availablePhysicalMemory);
                metrics.Add(new MetricDatum()
                    .WithMetricName("PhysicalMemoryAvailable")
                    .WithUnit("Bytes")
                    .WithValue(availablePhysicalMemory)
                    .WithDimensions(dimensions));
            }

            if (_isSubmitPhysicalMemoryUtilization)
            {
                Info("\tPhysical Memory Utilization: {0:F1}%", physicalMemoryUtilized);
                metrics.Add(new MetricDatum()
                    .WithMetricName("PhysicalMemoryUtilization")
                    .WithUnit("Percent")
                    .WithValue(physicalMemoryUtilized)
                    .WithDimensions(dimensions));
            }

            double availableVirtualMemory = computerInfo.AvailableVirtualMemory;
            double totalVirtualMemory = computerInfo.TotalVirtualMemory;
            double virtualMemoryUsed = (totalVirtualMemory - availableVirtualMemory);
            double virtualMemoryUtilized = (virtualMemoryUsed/totalVirtualMemory)*100;

            Info("\tTotal Virtual Memory: {0:N0} bytes", totalVirtualMemory);

            if (_isSubmitVirtualMemoryUsed)
            {
                Info("\tVirtual Memory Used: {0:N0} bytes", physicalMemoryUsed);
                metrics.Add(new MetricDatum()
                    .WithMetricName("VirtualMemoryUsed")
                    .WithUnit("Bytes")
                    .WithValue(virtualMemoryUsed)
                    .WithDimensions(dimensions));
            }

            if (_isSubmitVirtualMemoryAvailable)
            {
                Info("\tAvailable Virtual Memory: {0:N0} bytes", availableVirtualMemory);
                metrics.Add(new MetricDatum()
                    .WithMetricName("VirtualMemoryAvailable")
                    .WithUnit("Bytes")
                    .WithValue(availableVirtualMemory)
                    .WithDimensions(dimensions));
            }

            if (_isSubmitVirtualMemoryUtilization)
            {
                Info("\tVirtual Memory Utilization: {0:F1}%", virtualMemoryUtilized);
                metrics.Add(new MetricDatum()
                    .WithMetricName("VirtualMemoryUtilization")
                    .WithUnit("Percent")
                    .WithValue(virtualMemoryUtilized)
                    .WithDimensions(dimensions));
            }

            double availableMemory = availablePhysicalMemory + availableVirtualMemory;
            double totalMemory = totalPhysicalMemory + totalVirtualMemory;
            double memoryUsed = (totalMemory - availableMemory);
            double memoryUtilized = (memoryUsed/totalMemory)*100;

            Info("\tTotal Memory: {0:N0} bytes", totalMemory);

            if (_isSubmitMemoryUsed)
            {
                Info("\tMemory Used: {0:N0} bytes", physicalMemoryUsed);
                metrics.Add(new MetricDatum()
                    .WithMetricName("MemoryUsed")
                    .WithUnit("Bytes")
                    .WithValue(memoryUsed)
                    .WithDimensions(dimensions));
            }

            if (_isSubmitMemoryAvailable)
            {
                Info("\tAvailable Memory: {0:N0} bytes", availableMemory);
                metrics.Add(new MetricDatum()
                    .WithMetricName("MemoryAvailable")
                    .WithUnit("Bytes")
                    .WithValue(availableMemory)
                    .WithDimensions(dimensions));
            }

            if (_isSubmitMemoryUtilization)
            {
                Info("\tMemory Utilization: {0:F1}%", memoryUtilized);
                metrics.Add(new MetricDatum()
                    .WithMetricName("MemoryUtilization")
                    .WithUnit("Percent")
                    .WithValue(memoryUtilized)
                    .WithDimensions(dimensions));
            }
        }

        private void AddDriveMetrics(DriveInfo drive, List<MetricDatum> metrics)
        {
            string driveName = String.Format("{0}", drive.Name[0]);

            // If we need to filter drives, then only include those
            // explicitly specified.
            if (_includeDrives != null)
            {
                if (!_includeDrives.Contains(driveName))
                {
                    Info("Not including drive: {0}", driveName);
                    return;
                }
            }

            // For the drive,
            // collect the free space values we care about,
            // formulate the request objects,
            // submit to AWS
            Info("Adding metrics for drive: {0}", driveName);

            // Skip drives not ready
            if (!drive.IsReady)
            {
                Info("\tNot ready");
                return;
            }

            var dimensions = new List<Dimension>();
            dimensions.Add(new Dimension()
                .WithName("InstanceId")
                .WithValue(_instanceId));
            dimensions.Add(new Dimension()
                .WithName("Drive")
                .WithValue(driveName));

            long spaceAvailable = drive.AvailableFreeSpace;
            long totalSize = drive.TotalSize;
            long spaceUsed = drive.TotalSize - drive.AvailableFreeSpace;
            double diskUtilized;

            // If the drive has no size, then assume 100% used
            if (totalSize == 0)
                diskUtilized = 1.0;
            else
                diskUtilized = Convert.ToDouble(spaceUsed)/Convert.ToDouble(totalSize);
            diskUtilized *= 100.0;

            Info("\tTotal Disk Space: {0:N0} bytes", totalSize);

            if (_isSubmitDiskSpaceUsed)
            {
                Info("\tDisk Space Used: {0:N0} bytes", spaceUsed);
                metrics.Add(new MetricDatum()
                    .WithMetricName("DiskSpaceUsed")
                    .WithUnit("Bytes")
                    .WithValue(spaceUsed)
                    .WithDimensions(dimensions));
            }

            if (_isSubmitDiskSpaceAvailable)
            {
                Info("\tDisk Space Available: {0:N0} bytes", spaceAvailable);
                metrics.Add(new MetricDatum()
                    .WithMetricName("DiskSpaceAvailable")
                    .WithUnit("Bytes")
                    .WithValue(spaceAvailable)
                    .WithDimensions(dimensions));
            }

            if (_isSubmitDiskSpaceUtilization)
            {
                Info("\tDisk Space Utilization: {0:F1}%", diskUtilized);
                metrics.Add(new MetricDatum()
                    .WithMetricName("DiskSpaceUtilization")
                    .WithUnit("Percent")
                    .WithValue(diskUtilized)
                    .WithDimensions(dimensions));
            }
        }

        private bool PopulateInstanceId()
        {
            // If we've already got this value, then
            // just return true
            if (!String.IsNullOrEmpty(_instanceId))
                return true;

            // Call to AWS to get the current EC2 instance ID
            try
            {
                // Get the instance id
                var uri = new Uri("http://169.254.169.254/latest/meta-data/instance-id");

                var client = new WebClient();
                _instanceId = client.DownloadString(uri);

                Info("Instance ID: {0}", _instanceId);
                return true;
            }
            catch (Exception e)
            {
                Error("Error getting instance id: {0}", e.Message);
                return false;
            }
        }

        private bool PopulateRegion()
        {
            if (!String.IsNullOrEmpty(_region))
                return true;

            // Call to AWS to get the current availability zone
            string availabilityZone;
            try
            {
                // Get the instance id
                var uri = new Uri("http://169.254.169.254/latest/meta-data/placement/availability-zone");

                var client = new WebClient();
                availabilityZone = client.DownloadString(uri);

                Info("Availability Zone: {0}", availabilityZone);
            }
            catch (Exception e)
            {
                Error("Error getting availability zone: {0}", e.Message);
                return false;
            }

            // Assume that the region can be determined by stripping off the trailing a,b,c, etc.
            // This is ok now, but perhaps not in the future.
            _region = availabilityZone.Substring(0, availabilityZone.Length - 1);
            Info("Region: {0}", _region);

            return true;
        }

        private bool PopulateInstanceName()
        {
            if (!String.IsNullOrEmpty(_instanceName))
                return true;

            if (String.IsNullOrEmpty(_region))
            {
                PopulateRegion();
            }

            DescribeInstancesRequest request = new DescribeInstancesRequest()
                .WithInstanceId(_instanceId);

            RegionEndpoint regionEndpoint = RegionEndpoint.GetBySystemName(_region);
            var client = new AmazonEC2Client(regionEndpoint);
            DescribeInstancesResponse response = client.DescribeInstances(request);

            foreach (Tag tag in response.DescribeInstancesResult.Reservation[0].RunningInstance[0].Tag)
            {
                if (tag.IsSetKey() && tag.IsSetValue())
                {
                    if (tag.Key == "Name")
                    {
                        _instanceName = tag.Value;
                    }
                }
            }
            return true;
        }
    }
}