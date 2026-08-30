using CommandLine;
using Gommon;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using Ryujinx.Common.SystemInterop;
using Ryujinx.Headless.UI;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Headless
{
    public static class Program
    {
        public static string Version => "1.0.0-Ryu-Darwin";

        public static void Main(string[] args)
        {
            // Set terminal title
            Console.Title = $"Ryu Headless Engine v{Version}";

            // Enable macOS Game Mode hints
            if (OperatingSystem.IsMacOS())
            {
                DarwinGameMode.TryEnableGameMode();
            }

            PrintSplash();
            PrintSystemInfo();

            if (args.Length == 0)
            {
                Console.WriteLine("\u001b[1;33mUsage:\u001b[0m Ryu [game_path] [options]");
                Console.WriteLine("Try 'Ryu --help' for all available options.\n");
                return;
            }

            HeadlessRyujinx.Entrypoint(args);
        }

        public static void PrintSplash()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(@"
   ____                 
  / __ \__  ____  __   
 / /_/ / / / / / / /   
/ _, _/ /_/ / /_/ /    
/_/ |_|\__, /\__,_/     
      /____/            
  Native Apple Silicon Headless Emulation Core
");
            Console.ResetColor();
        }

        public static void PrintSystemInfo()
        {
            Logger.Info?.Print(LogClass.Application, $"Ryu Headless Version: {Version}");
            Logger.Info?.Print(LogClass.Application, $".NET Runtime: {Environment.Version}");
            Logger.Info?.Print(LogClass.Application, $"Operating System: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            Logger.Info?.Print(LogClass.Application, $"Host CPU: {System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Apple Silicon"} ({Environment.ProcessorCount} cores)");
            Logger.Info?.Print(LogClass.Application, $"Total RAM: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024 * 1024.0):F1} GiB");
        }

        public static void ProcessUnhandledException(object sender, Exception ex, bool isTerminating)
        {
            Logger.Error?.Print(LogClass.Application, $"Unhandled exception caught: {ex}");
            if (isTerminating)
            {
                Logger.Error?.Print(LogClass.Application, "Fatal unhandled exception, terminating process.");
            }
        }

        public static void Exit()
        {
            TerminalHud.Stop();
            Logger.Info?.Print(LogClass.Application, "Ryu emulator process shutdown cleanly.");
        }
    }
}
