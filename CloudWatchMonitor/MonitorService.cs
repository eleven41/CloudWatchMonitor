using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Configuration;
using Amazon.CloudWatch;

namespace CloudWatchMonitor
{
	public partial class MonitorService : ServiceBase
	{
		// Constants
		const string CloudWatchNamespace = "System/Windows";

		// When we run as a service, we want errors
		// to be directed to an event log
		EventLog _eventLog = null;

		// The event upon which we wait for our signal to stop
		// the service.
		ManualResetEvent _evStop = new ManualResetEvent(false);

		// Instance ID of the current running instance.
		// Will be populated by communicating with metadata server.
		string _instanceId;

		// Region of the current running instance.
		// Will be populated by communicating with the metadata server.
		string _regionName;

		public MonitorService()
		{
			InitializeComponent();
		}

		protected override void OnStart(string[] args)
		{
			// Since we are running as a service, setup an event log
			string eventLogSource = "DiskSpaceCloudWatchMonitor";
			if (!EventLog.SourceExists(eventLogSource))
			{
				// Requires to be administrator for this to succeed
				EventLog.CreateEventSource(eventLogSource, "Eleven41");
			}
			_eventLog = new EventLog();
			_eventLog.Source = eventLogSource;

			// Start our main worker thread
			new System.Threading.Thread(new ThreadStart(Run)).Start();

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
			else
			{
				// Don't forward informational messages to the event log
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
				

			bool result = false;
			if (!Boolean.TryParse(temp, out result))
				throw new Exception(String.Format("{0} must be True or False: {1}", name, temp));
			
			return result;
		}

		private int ReadInt(string name, int defaultValue)
		{
			string temp = ConfigurationManager.AppSettings[name];
			if (String.IsNullOrEmpty(temp))
				return defaultValue;

			int result = 0;
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

		// Disk metrics
		List<string> _includeDrives;
		bool _isSubmitDiskSpaceAvailable;
		bool _isSubmitDiskSpaceUsed;
		bool _isSubmitDiskSpaceUtilization;

		// Memory metrics
		bool _isSubmitMemoryAvailable;
		bool _isSubmitMemoryUsed;
		bool _isSubmitMemoryUtilization;
		bool _isSubmitPhysicalMemoryAvailable;
		bool _isSubmitPhysicalMemoryUsed;
		bool _isSubmitPhysicalMemoryUtilization;
		bool _isSubmitVirtualMemoryAvailable;
		bool _isSubmitVirtualMemoryUsed;
		bool _isSubmitVirtualMemoryUtilization;
		
		// Primary running loop for the service
		public void Run()
		{
			Info("CloudWatch Monitor starting");
			
			// Default monitor period is 1 minute
			int monitorPeriodInMinutes = 1;

			try
			{
				Info("Reading configuration");

				monitorPeriodInMinutes = ReadInt("MonitorPeriodInMinutes", 1);

				// Validate min/max values
				if (monitorPeriodInMinutes < 1)
					throw new Exception("MonitorPeriodInMinutes must be greater than or equal to 1");
				Info("MonitorPeriodInMinutes: {0}", monitorPeriodInMinutes);

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

				_regionName = ReadString("AWSRegion", null);
				if (!String.IsNullOrEmpty(_regionName))
					Info("Region: {0}", _regionName);
			}
			catch (Exception e)
			{
				Error(e.Message);
				if (!Environment.UserInteractive)
					this.Stop(); // Tell the service to stop
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
					this.Stop(); // Tell the service to stop
				return;
			}

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

				TimeSpan baseTimeSpan = TimeSpan.FromMinutes(monitorPeriodInMinutes);

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

		private void UpdateMetrics()
		{
			if (!PopulateInstanceId())
				return;

			if (!PopulateRegion())
				return;

			List<Amazon.CloudWatch.Model.MetricDatum> metrics = new List<Amazon.CloudWatch.Model.MetricDatum>();
			
			if (_isSubmitDiskSpaceAvailable ||
				_isSubmitDiskSpaceUsed ||
				_isSubmitDiskSpaceUtilization)
			{
				// Get the list of drives from the system
				var drives = System.IO.DriveInfo.GetDrives();
				foreach (var drive in drives)
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

			var client = CreateClient();

			for (int skip = 0; ; skip += 20)
			{
				var metricsThisRound = metrics.Skip(skip).Take(20);
				if (!metricsThisRound.Any())
					break;

				var request = new Amazon.CloudWatch.Model.PutMetricDataRequest()
				{
					Namespace = CloudWatchNamespace,
					MetricData = metricsThisRound.ToList()
				};
				var response = client.PutMetricData(request);
			}

			// We don't care about the response

			Info("Done.");
		}

		private IAmazonCloudWatch CreateClient()
		{
			// Submit in the region that was specified in the config file.
			AmazonCloudWatchConfig config = new AmazonCloudWatchConfig()
			{
				RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_regionName)
			};

			return new AmazonCloudWatchClient(config);
		}

		private void SubmitMemoryMetrics(List<Amazon.CloudWatch.Model.MetricDatum> metrics)
		{
			Info("Adding memory metrics");

			var dimensions = new List<Amazon.CloudWatch.Model.Dimension>();
			dimensions.Add(new Amazon.CloudWatch.Model.Dimension()
			{
				Name = "InstanceId", 
				Value = _instanceId
			});

			// Why is this in a visual basic namespace?
			var computerInfo = new Microsoft.VisualBasic.Devices.ComputerInfo();

			double availablePhysicalMemory = computerInfo.AvailablePhysicalMemory;
			double totalPhysicalMemory = computerInfo.TotalPhysicalMemory;
			double physicalMemoryUsed = (totalPhysicalMemory - availablePhysicalMemory);
			double physicalMemoryUtilized = (physicalMemoryUsed / totalPhysicalMemory) * 100;

			Info("\tTotal Physical Memory: {0:N0} bytes", totalPhysicalMemory);

			if (_isSubmitPhysicalMemoryUsed)
			{
				Info("\tPhysical Memory Used: {0:N0} bytes", physicalMemoryUsed);
				metrics.Add(new Amazon.CloudWatch.Model.MetricDatum()
				{
					MetricName = "PhysicalMemoryUsed",
					Unit = "Bytes",
					Value = physicalMemoryUsed,
					Dimensions = dimensions
				});
			}

			if (_isSubmitPhysicalMemoryAvailable)
			{
				Info("\tAvailable Physical Memory: {0:N0} bytes", availablePhysicalMemory);
				metrics.Add(new Amazon.CloudWatch.Model.MetricDatum()
				{
					MetricName = "PhysicalMemoryAvailable",
					Unit = "Bytes",
					Value = availablePhysicalMemory,
					Dimensions = dimensions
				});
			}

			if (_isSubmitPhysicalMemoryUtilization)
			{
				Info("\tPhysical Memory Utilization: {0:F1}%", physicalMemoryUtilized);
				metrics.Add(new Amazon.CloudWatch.Model.MetricDatum()
				{
					MetricName = "PhysicalMemoryUtilization",
					Unit = "Percent",
					Value = physicalMemoryUtilized,
					Dimensions = dimensions
				});
			}

			double availableVirtualMemory = computerInfo.AvailableVirtualMemory;
			double totalVirtualMemory = computerInfo.TotalVirtualMemory;
			double virtualMemoryUsed = (totalVirtualMemory - availableVirtualMemory);
			double virtualMemoryUtilized = (virtualMemoryUsed / totalVirtualMemory) * 100;

			Info("\tTotal Virtual Memory: {0:N0} bytes", totalVirtualMemory);

			if (_isSubmitVirtualMemoryUsed)
			{
				Info("\tVirtual Memory Used: {0:N0} bytes", physicalMemoryUsed);
				metrics.Add(new Amazon.CloudWatch.Model.MetricDatum()
				{
					MetricName = "VirtualMemoryUsed",
					Unit = "Bytes",
					Value = virtualMemoryUsed,
					Dimensions = dimensions
				});
			}

			if (_isSubmitVirtualMemoryAvailable)
			{
				Info("\tAvailable Virtual Memory: {0:N0} bytes", availableVirtualMemory);
				metrics.Add(new Amazon.CloudWatch.Model.MetricDatum()
				{
					MetricName = "VirtualMemoryAvailable",
					Unit = "Bytes",
					Value = availableVirtualMemory,
					Dimensions = dimensions
				});
			}

			if (_isSubmitVirtualMemoryUtilization)
			{
				Info("\tVirtual Memory Utilization: {0:F1}%", virtualMemoryUtilized);
				metrics.Add(new Amazon.CloudWatch.Model.MetricDatum()
				{
					MetricName = "VirtualMemoryUtilization",
					Unit = "Percent",
					Value = virtualMemoryUtilized,
					Dimensions = dimensions
				});
			}

			double availableMemory = availablePhysicalMemory + availableVirtualMemory;
			double totalMemory = totalPhysicalMemory + totalVirtualMemory;
			double memoryUsed = (totalMemory - availableMemory);
			double memoryUtilized = (memoryUsed / totalMemory) * 100;

			Info("\tTotal Memory: {0:N0} bytes", totalMemory);

			if (_isSubmitMemoryUsed)
			{
				Info("\tMemory Used: {0:N0} bytes", physicalMemoryUsed);
				metrics.Add(new Amazon.CloudWatch.Model.MetricDatum()
				{
					MetricName = "MemoryUsed",
					Unit = "Bytes",
					Value = memoryUsed,
					Dimensions = dimensions
				});
			}

			if (_isSubmitMemoryAvailable)
			{
				Info("\tAvailable Memory: {0:N0} bytes", availableMemory);
				metrics.Add(new Amazon.CloudWatch.Model.MetricDatum()
				{
					MetricName = "MemoryAvailable",
					Unit = "Bytes",
					Value = availableMemory,
					Dimensions = dimensions
				});
			}

			if (_isSubmitMemoryUtilization)
			{
				Info("\tMemory Utilization: {0:F1}%", memoryUtilized);
				metrics.Add(new Amazon.CloudWatch.Model.MetricDatum()
				{
					MetricName = "MemoryUtilization",
					Unit = "Percent",
					Value = memoryUtilized,
					Dimensions = dimensions
				});
			}
		}

		private void AddDriveMetrics(System.IO.DriveInfo drive, List<Amazon.CloudWatch.Model.MetricDatum> metrics)
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

			var dimensions = new List<Amazon.CloudWatch.Model.Dimension>();
			dimensions.Add(new Amazon.CloudWatch.Model.Dimension()
			{
				Name = "InstanceId",
				Value = _instanceId
			});
			dimensions.Add(new Amazon.CloudWatch.Model.Dimension()
			{
				Name = "Drive",
				Value = driveName
			});

			long spaceAvailable = drive.AvailableFreeSpace;
			long totalSize = drive.TotalSize;
			long spaceUsed = drive.TotalSize - drive.AvailableFreeSpace;
			double diskUtilized;

			// If the drive has no size, then assume 100% used
			if (totalSize == 0)
				diskUtilized = 1.0;
			else
				diskUtilized = Convert.ToDouble(spaceUsed) / Convert.ToDouble(totalSize);
			diskUtilized *= 100.0;

			Info("\tTotal Disk Space: {0:N0} bytes", totalSize);

			if (_isSubmitDiskSpaceUsed)
			{
				Info("\tDisk Space Used: {0:N0} bytes", spaceUsed);
				metrics.Add(new Amazon.CloudWatch.Model.MetricDatum()
				{
					MetricName = "DiskSpaceUsed",
					Unit = "Bytes",
					Value = spaceUsed,
					Dimensions = dimensions
				});
			}

			if (_isSubmitDiskSpaceAvailable)
			{
				Info("\tDisk Space Available: {0:N0} bytes", spaceAvailable);
				metrics.Add(new Amazon.CloudWatch.Model.MetricDatum()
				{
					MetricName = "DiskSpaceAvailable",
					Unit = "Bytes",
					Value = spaceAvailable,
					Dimensions = dimensions
				});
			}

			if (_isSubmitDiskSpaceUtilization)
			{
				Info("\tDisk Space Utilization: {0:F1}%", diskUtilized);
				metrics.Add(new Amazon.CloudWatch.Model.MetricDatum()
				{
					MetricName = "DiskSpaceUtilization",
					Unit = "Percent",
					Value = diskUtilized,
					Dimensions = dimensions
				});
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
				Uri uri = new Uri("http://169.254.169.254/latest/meta-data/instance-id");

				var client = new System.Net.WebClient();
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
			if (!String.IsNullOrEmpty(_regionName))
				return true;

			// Call to AWS to get the current availability zone
			string availabilityZone;
			try
			{
				// Get the instance id
				Uri uri = new Uri("http://169.254.169.254/latest/meta-data/placement/availability-zone");

				var client = new System.Net.WebClient();
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
			_regionName = availabilityZone.Substring(0, availabilityZone.Length - 1);
			Info("Region: {0}", _regionName);

			return true;
		}
	}
}
