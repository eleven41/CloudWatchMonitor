using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Configuration.Install;
using System.Reflection;

namespace CloudWatchMonitor
{
	static class Program
	{
		/// <summary>
		/// The main entry point for the application.
		/// </summary>
		static void Main(string[] args)
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
							ManagedInstallerClass.InstallHelper(new string[] { Assembly.GetExecutingAssembly().Location });
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
							ManagedInstallerClass.InstallHelper(new string[] { "/u", Assembly.GetExecutingAssembly().Location });
						}
						catch (Exception e)
						{

							Console.WriteLine("Error uninstalling service: {0}", e.Message);
						}
						return;
				}

				service.Run();
				return;
			}

			ServiceBase[] ServicesToRun;
			ServicesToRun = new ServiceBase[] 
			{ 
				service
			};
			ServiceBase.Run(ServicesToRun);
		}
	}
}
