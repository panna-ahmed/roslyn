﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AnalyzerRunner;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.NavigateTo;
using Microsoft.CodeAnalysis.Storage;

namespace IdeCoreBenchmarks
{
    [MemoryDiagnoser]
    public class NavigateToBenchmarks
    {
        string _solutionPath;
        MSBuildWorkspace _workspace;

        [GlobalSetup]
        public void GlobalSetup()
        {
            RestoreCompilerSolution();
            SetUpWorkspace();
        }

        [IterationSetup]
        public void IterationSetup() => LoadSolutionAsync().Wait();

        private void RestoreCompilerSolution()
        {
            var roslynRoot = Environment.GetEnvironmentVariable(Program.RoslynRootPathEnvVariableName);
            _solutionPath = Path.Combine(roslynRoot, @"Roslyn.sln");
            var restoreOperation = Process.Start("dotnet", $"restore /p:UseSharedCompilation=false /p:BuildInParallel=false /m:1 /p:Deterministic=true /p:Optimize=true {_solutionPath}");
            restoreOperation.WaitForExit();
            if (restoreOperation.ExitCode != 0)
                throw new ArgumentException($"Unable to restore {_solutionPath}");
        }

        private static void SetUpWorkspace()
        {
            // QueryVisualStudioInstances returns Visual Studio installations on .NET Framework, and .NET Core SDK
            // installations on .NET Core. We use the one with the most recent version.
            var msBuildInstance = MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(x => x.Version).First();

            MSBuildLocator.RegisterInstance(msBuildInstance);
        }

        private async Task LoadSolutionAsync()
        {
            var roslynRoot = Environment.GetEnvironmentVariable(Program.RoslynRootPathEnvVariableName);
            _solutionPath = Path.Combine(roslynRoot, @"Roslyn.sln");

            if (!File.Exists(_solutionPath))
                throw new ArgumentException("Couldn't find Roslyn.sln");

            Console.Write("Found Roslyn.sln: " + Process.GetCurrentProcess().Id);
            var assemblies = MSBuildMefHostServices.DefaultAssemblies
                .Add(typeof(AnalyzerRunnerHelper).Assembly)
                .Add(typeof(FindReferencesBenchmarks).Assembly);
            var services = MefHostServices.Create(assemblies);

            _workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
                {
                    // Use the latest language version to force the full set of available analyzers to run on the project.
                    { "LangVersion", "9.0" },
                }, services);

            if (_workspace == null)
                throw new ArgumentException("Couldn't create workspace");

            _workspace.TryApplyChanges(_workspace.CurrentSolution.WithOptions(_workspace.Options
                .WithChangedOption(StorageOptions.Database, StorageDatabase.SQLite)));

            Console.WriteLine("Opening roslyn.  Attach to: " + Process.GetCurrentProcess().Id);

            var start = DateTime.Now;
            var solution = _workspace.OpenSolutionAsync(_solutionPath, progress: null, CancellationToken.None).Result;
            Console.WriteLine("Finished opening roslyn: " + (DateTime.Now - start));

            // Force a storage instance to be created.  This makes it simple to go examine it prior to any operations we
            // perform, including seeing how big the initial string table is.
            var storageService = _workspace.Services.GetPersistentStorageService(_workspace.CurrentSolution.Options);
            if (storageService == null)
                throw new ArgumentException("Couldn't get storage service");

            using (var storage = await storageService.GetStorageAsync(SolutionKey.ToSolutionKey(_workspace.CurrentSolution), CancellationToken.None))
            {
                Console.WriteLine("Sucessfully got persistent storage instance");
            }
        }

        [IterationCleanup]
        public void IterationCleanup()
        {
            _workspace.Dispose();
            _workspace = null;
        }

        [Benchmark]

        public async Task RunNavigateTo()
        {
            Console.WriteLine("Starting navigate to");

            var start = DateTime.Now;
            // Search each project with an independent threadpool task.
            var searchTasks = _workspace.CurrentSolution.Projects.Select(
                p => Task.Run(() => SearchAsync(p, priorityDocuments: ImmutableArray<Document>.Empty), CancellationToken.None)).ToArray();

            var result = await Task.WhenAll(searchTasks).ConfigureAwait(false);
            var sum = result.Sum();

            //start = DateTime.Now;
            Console.WriteLine("Num results: " + (DateTime.Now - start));
        }

        private async Task<int> SearchAsync(Project project, ImmutableArray<Document> priorityDocuments)
        {
            var service = project.LanguageServices.GetService<INavigateToSearchService>();
            var results = new List<INavigateToSearchResult>();
            await service.SearchProjectAsync(
                project, priorityDocuments, "Syntax", service.KindsProvided,
                r =>
                {
                    lock (results)
                        results.Add(r);

                    return Task.CompletedTask;
                }, isFullyLoaded: true, CancellationToken.None);

            return results.Count;
        }
    }
}
