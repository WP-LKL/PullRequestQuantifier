﻿namespace PullRequestQuantifier.Local.Client
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using global::PullRequestQuantifier.Client;
    using LibGit2Sharp;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;

    /// <summary>
    /// Parameters accepted: GitRepoPath={} ContextPath={} Service=True/False(default is false) PrintJson=True/False(default is false).
    /// </summary>
    public static class Program
    {
        private static ServiceProvider serviceProvider;
        private static DateTimeOffset changedEventDateTime = DateTimeOffset.MaxValue;

        public static async Task Main(string[] args)
        {
            var builder = new ConfigurationBuilder();
            builder.AddCommandLine(args);
            var config = builder.Build();

            var gitRepoPath = config["GitRepoPath"] ?? Environment.CurrentDirectory;

            var repoRootPath = Repository.Discover(gitRepoPath);

            // if no repo was found  to this path then exit, don't crash
            if (string.IsNullOrWhiteSpace(repoRootPath))
            {
                Console.WriteLine("GitRepoPath couldn't be found!");
                return;
            }

            var contextPath = config["ContextPath"]
                              ?? Path.Combine(
                                  new DirectoryInfo(repoRootPath).Parent?.FullName,
                                  ".prquantifier");
            Dependencies(
                contextPath,
                gitRepoPath,
                config["PrintJson"] ?? "false");

            // run this as a service in case is configured otherwise only run once
            var service = config["Service"];
            if (!string.IsNullOrWhiteSpace(service) &&
                service.Equals("true", StringComparison.InvariantCultureIgnoreCase))
            {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                Task.Factory.StartNew(Quantify);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

                await Run(gitRepoPath, contextPath);
            }

            // in case service is not set or false then run once and exit
            await serviceProvider.GetService<IQuantifyClient>().Compute();
        }

        private static void Quantify()
        {
            while (true)
            {
                if ((DateTimeOffset.Now - changedEventDateTime).TotalSeconds < 1)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(100));
                    continue;
                }

                serviceProvider.GetService<IQuantifyClient>()?.Compute().Wait();
                changedEventDateTime = DateTimeOffset.MaxValue;
            }
        }

        private static void Dependencies(
            string contextFilePath,
            string gitRepoPath,
            string printJson)
        {
            bool.TryParse(printJson, out var jsonFormat);

            serviceProvider = new ServiceCollection()
                .AddSingleton<IQuantifyClient>(p => new QuantifyClient(
                    gitRepoPath,
                    contextFilePath,
                    jsonFormat))
                .BuildServiceProvider();
        }

        private static async Task Run(
            string gitRepoPath,
            string contextFilePath)
        {
            await Task.Factory.StartNew(() =>
            {
                // Create a new FileSystemWatcher and set its properties.
                using FileSystemWatcher watcher = new FileSystemWatcher
                {
                    Path = new DirectoryInfo(Repository.Discover(gitRepoPath)).Parent
                        .FullName,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    IncludeSubdirectories = true,
                    Filter = "*.*"
                };

                // Add event handlers.
                watcher.Changed += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Created += OnChanged;

                // Begin watching.
                watcher.EnableRaisingEvents = true;

                // Wait for the user to quit the program.
                Console.WriteLine("Press 'q' to quit, 'c' to see the team context, 't' to see the configured thresholds.");
                int key;
                while ((key = Console.Read()) != 'q')
                {
                    switch (key)
                    {
                        case 'c':
                        {
                            Console.ForegroundColor = ConsoleColor.Blue;
                            Console.WriteLine();
                            Console.WriteLine("Quantifier Context:");
                            Console.WriteLine(File.ReadAllText(contextFilePath));
                            Console.ResetColor();
                            break;
                        }

                        case 'p':
                        {
                            PrintPercentiles(true);
                            PrintPercentiles(false);
                            break;
                        }

                        case 't':
                        {
                            PrintThresholds();
                            break;
                        }
                    }
                }
            });
        }

        // Define the event handlers.
        private static void OnChanged(object source, FileSystemEventArgs e)
        {
            changedEventDateTime = DateTimeOffset.Now;
        }

        private static void PrintThresholds()
        {
            var thresholds = serviceProvider?.GetService<IQuantifyClient>()?.Context.Thresholds;
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine();
            Console.WriteLine("Thresholds: " + string.Join(" ", thresholds.Select(t => $"\"{t.Label} = {t.Value}\"")));
            Console.ResetColor();
        }

        private static void PrintPercentiles(bool addition)
        {
            var quantifyClient = serviceProvider?.GetService<IQuantifyClient>();
            var percentile = addition ? quantifyClient.Context.AdditionPercentile : quantifyClient.Context.DeletionPercentile;
            string label = addition ? "Addition" : "Deletion";

            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine();
            Console.WriteLine($"{label} Percentile:");
            Console.WriteLine(string.Join(" ", percentile.Select(t => $"{t.Key} = {t.Value}\n")));
            Console.ResetColor();
        }
    }
}