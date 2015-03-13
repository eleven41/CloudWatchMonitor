using System;
using System.Configuration.Install;
using System.Reflection;
using System.ServiceProcess;

namespace CloudWatchMonitor
{
    internal static class Program
    {
        /// <summary>
        ///     The main entry point for the application.
        /// </summary>
        private static void Main(string[] args)
        {
            var service = new MonitorService();

            if (Environment.UserInteractive)
            {
                string parameter = string.Concat(args);
                switch (parameter)
                {
                    case "-i":
                        try
                        {
                            Console.WriteLine("Installing service...");
                            ManagedInstallerClass.InstallHelper(new[] {Assembly.GetExecutingAssembly().Location});
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Error installing service: {0}", e.Message);
                        }
                        return;

                    case "-u":
                        try
                        {
                            Console.WriteLine("Uninstalling service...");
                            ManagedInstallerClass.InstallHelper(new[] {"/u", Assembly.GetExecutingAssembly().Location});
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Error uninstalling service: {0}", e.Message);
                        }
                        return;

                    case "-a":
                        try
                        {
                            Console.WriteLine("Creating AWS alarms...");
                            service.CreateAlarms();
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Error creating alarms: {0}", e.Message);
                        }
                        return;
                }

                service.Run();
                return;
            }

            var servicesToRun = new ServiceBase[]
            {
                service
            };
            ServiceBase.Run(servicesToRun);
        }
    }
}