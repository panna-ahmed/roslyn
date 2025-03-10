﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.CodeAnalysis.Remote.Testing;
using Microsoft.CodeAnalysis.Serialization;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Roslyn.VisualStudio.Next.UnitTests.Remote
{
    [UseExportProvider]
    [Trait(Traits.Feature, Traits.Features.RemoteHost)]
    public class SolutionServiceTests
    {
        private static RemoteWorkspace CreateRemoteWorkspace()
            => new RemoteWorkspace(FeaturesTestCompositions.RemoteHost.GetHostServices(), WorkspaceKind.RemoteWorkspace);

        [Fact]
        public async Task TestCreation()
        {
            var code = @"class Test { void Method() { } }";

            using var workspace = TestWorkspace.CreateCSharp(code);
            using var remoteWorkspace = CreateRemoteWorkspace();

            var solution = workspace.CurrentSolution;
            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);

            var solutionChecksum = await solution.State.GetChecksumAsync(CancellationToken.None);
            var synched = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: false, workspaceVersion: -1, projectId: null, CancellationToken.None);

            Assert.Equal(solutionChecksum, await synched.State.GetChecksumAsync(CancellationToken.None));
        }

        [Theory]
        [CombinatorialData]
        public async Task TestGetSolutionWithPrimaryFlag(bool fromPrimaryBranch)
        {
            var code1 = @"class Test1 { void Method() { } }";

            using var workspace = TestWorkspace.CreateCSharp(code1);
            using var remoteWorkspace = CreateRemoteWorkspace();

            var solution = workspace.CurrentSolution;
            var solutionChecksum = await solution.State.GetChecksumAsync(CancellationToken.None);
            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);

            var synched = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch, solution.WorkspaceVersion, projectId: null, cancellationToken: CancellationToken.None);
            Assert.Equal(solutionChecksum, await synched.State.GetChecksumAsync(CancellationToken.None));

            if (fromPrimaryBranch)
            {
                Assert.IsType<RemoteWorkspace>(synched.Workspace);
            }
            else
            {
                Assert.IsType<TemporaryWorkspace>(synched.Workspace);
            }
        }

        [Fact]
        public async Task TestStrongNameProvider()
        {
            using var workspace = new AdhocWorkspace();
            using var remoteWorkspace = CreateRemoteWorkspace();

            var filePath = typeof(SolutionServiceTests).Assembly.Location;

            workspace.AddProject(
                ProjectInfo.Create(
                    ProjectId.CreateNewId(), VersionStamp.Create(), "test", "test.dll", LanguageNames.CSharp,
                    filePath: filePath, outputFilePath: filePath));

            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, workspace.CurrentSolution);

            var solutionChecksum = await workspace.CurrentSolution.State.GetChecksumAsync(CancellationToken.None);
            var solution = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: false, workspaceVersion: -1, projectId: null, CancellationToken.None);

            var compilationOptions = solution.Projects.First().CompilationOptions;

            Assert.IsType<DesktopStrongNameProvider>(compilationOptions.StrongNameProvider);
            Assert.IsType<XmlFileResolver>(compilationOptions.XmlReferenceResolver);

            var dirName = PathUtilities.GetDirectoryName(filePath);
            var array = new[] { dirName, dirName };
            Assert.Equal(Hash.CombineValues(array, StringComparer.Ordinal), compilationOptions.StrongNameProvider.GetHashCode());
            Assert.Equal(((XmlFileResolver)compilationOptions.XmlReferenceResolver).BaseDirectory, dirName);
        }

        [Fact]
        public async Task TestStrongNameProviderEmpty()
        {
            using var workspace = new AdhocWorkspace();
            using var remoteWorkspace = CreateRemoteWorkspace();

            var filePath = "testLocation";

            workspace.AddProject(
                ProjectInfo.Create(
                    ProjectId.CreateNewId(), VersionStamp.Create(), "test", "test.dll", LanguageNames.CSharp,
                    filePath: filePath, outputFilePath: filePath));

            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, workspace.CurrentSolution);

            var solutionChecksum = await workspace.CurrentSolution.State.GetChecksumAsync(CancellationToken.None);
            var solution = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: false, workspaceVersion: -1, projectId: null, CancellationToken.None);

            var compilationOptions = solution.Projects.First().CompilationOptions;

            Assert.True(compilationOptions.StrongNameProvider is DesktopStrongNameProvider);
            Assert.True(compilationOptions.XmlReferenceResolver is XmlFileResolver);

            var array = new string[] { };
            Assert.Equal(Hash.CombineValues(array, StringComparer.Ordinal), compilationOptions.StrongNameProvider.GetHashCode());
            Assert.Null(((XmlFileResolver)compilationOptions.XmlReferenceResolver).BaseDirectory);
        }

        [Fact]
        public async Task TestCache()
        {
            var code = @"class Test { void Method() { } }";

            using var workspace = TestWorkspace.CreateCSharp(code);
            using var remoteWorkspace = CreateRemoteWorkspace();

            var solution = workspace.CurrentSolution;
            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);
            var solutionChecksum = await solution.State.GetChecksumAsync(CancellationToken.None);

            var first = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: false, workspaceVersion: -1, projectId: null, CancellationToken.None);
            var second = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: false, workspaceVersion: -1, projectId: null, CancellationToken.None);

            // same instance from cache
            Assert.True(object.ReferenceEquals(first, second));
            Assert.True(first.Workspace is TemporaryWorkspace);
        }

        [Fact]
        public async Task TestUpdatePrimaryWorkspace()
        {
            var code = @"class Test { void Method() { } }";

            await VerifySolutionUpdate(code, s => s.WithDocumentText(s.Projects.First().DocumentIds.First(), SourceText.From(code + " ")));
        }

        [Fact]
        public async Task ProjectProperties()
        {
            using var workspace = TestWorkspace.CreateCSharp("");

            static Solution SetProjectProperties(Solution solution, int version)
            {
                var projectId = solution.ProjectIds.Single();
                return solution
                    .WithProjectName(projectId, "Name" + version)
                    .WithProjectAssemblyName(projectId, "AssemblyName" + version)
                    .WithProjectFilePath(projectId, "FilePath" + version)
                    .WithProjectOutputFilePath(projectId, "OutputFilePath" + version)
                    .WithProjectOutputRefFilePath(projectId, "OutputRefFilePath" + version)
                    .WithProjectCompilationOutputInfo(projectId, new CompilationOutputInfo("AssemblyPath" + version))
                    .WithProjectDefaultNamespace(projectId, "DefaultNamespace" + version)
                    .WithHasAllInformation(projectId, (version % 2) != 0)
                    .WithRunAnalyzers(projectId, (version % 2) != 0);
            }

            static void ValidateProperties(Solution solution, int version)
            {
                var project = solution.Projects.Single();
                Assert.Equal("Name" + version, project.Name);
                Assert.Equal("AssemblyName" + version, project.AssemblyName);
                Assert.Equal("FilePath" + version, project.FilePath);
                Assert.Equal("OutputFilePath" + version, project.OutputFilePath);
                Assert.Equal("OutputRefFilePath" + version, project.OutputRefFilePath);
                Assert.Equal("AssemblyPath" + version, project.CompilationOutputInfo.AssemblyPath);
                Assert.Equal("DefaultNamespace" + version, project.DefaultNamespace);
                Assert.Equal((version % 2) != 0, project.State.HasAllInformation);
                Assert.Equal((version % 2) != 0, project.State.RunAnalyzers);
            }

            Assert.True(workspace.SetCurrentSolution(s => SetProjectProperties(s, version: 0), WorkspaceChangeKind.SolutionChanged));

            await VerifySolutionUpdate(workspace,
                newSolutionGetter: s => SetProjectProperties(s, version: 1),
                oldSolutionValidator: s => ValidateProperties(s, version: 0),
                newSolutionValidator: s => ValidateProperties(s, version: 1)).ConfigureAwait(false);
        }

        [Fact]
        public async Task TestUpdateDocumentInfo()
        {
            var code = @"class Test { void Method() { } }";

            await VerifySolutionUpdate(code, s => s.WithDocumentFolders(s.Projects.First().Documents.First().Id, new[] { "test" }));
        }

        [Fact]
        public async Task TestAddUpdateRemoveProjects()
        {
            var code = @"class Test { void Method() { } }";

            await VerifySolutionUpdate(code, s =>
            {
                var existingProjectId = s.ProjectIds.First();

                s = s.AddProject("newProject", "newProject", LanguageNames.CSharp).Solution;

                var project = s.GetProject(existingProjectId);
                project = project.WithCompilationOptions(project.CompilationOptions.WithModuleName("modified"));

                var existingDocumentId = project.DocumentIds.First();

                project = project.AddDocument("newDocument", SourceText.From("// new text")).Project;

                var document = project.GetDocument(existingDocumentId);

                document = document.WithSourceCodeKind(SourceCodeKind.Script);

                return document.Project.Solution;
            });
        }

        [Fact]
        public async Task TestAdditionalDocument()
        {
            var code = @"class Test { void Method() { } }";
            using var workspace = TestWorkspace.CreateCSharp(code);

            var projectId = workspace.CurrentSolution.ProjectIds.First();
            var additionalDocumentId = DocumentId.CreateNewId(projectId);
            var additionalDocumentInfo = DocumentInfo.Create(
                additionalDocumentId, "additionalFile",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From("test"), VersionStamp.Create())));

            await VerifySolutionUpdate(workspace, s =>
            {
                return s.AddAdditionalDocument(additionalDocumentInfo);
            });

            workspace.OnAdditionalDocumentAdded(additionalDocumentInfo);

            await VerifySolutionUpdate(workspace, s =>
            {
                return s.WithAdditionalDocumentText(additionalDocumentId, SourceText.From("changed"));
            });

            await VerifySolutionUpdate(workspace, s =>
            {
                return s.RemoveAdditionalDocument(additionalDocumentId);
            });
        }

        [Fact]
        public async Task TestAnalyzerConfigDocument()
        {
            var configPath = Path.Combine(Path.GetTempPath(), ".editorconfig");
            var code = @"class Test { void Method() { } }";
            using var workspace = TestWorkspace.CreateCSharp(code);

            var projectId = workspace.CurrentSolution.ProjectIds.First();
            var analyzerConfigDocumentId = DocumentId.CreateNewId(projectId);
            var analyzerConfigDocumentInfo = DocumentInfo.Create(
                analyzerConfigDocumentId,
                name: ".editorconfig",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From("root = true"), VersionStamp.Create(), filePath: configPath)),
                filePath: configPath);

            await VerifySolutionUpdate(workspace, s =>
            {
                return s.AddAnalyzerConfigDocuments(ImmutableArray.Create(analyzerConfigDocumentInfo));
            });

            workspace.OnAnalyzerConfigDocumentAdded(analyzerConfigDocumentInfo);

            await VerifySolutionUpdate(workspace, s =>
            {
                return s.WithAnalyzerConfigDocumentText(analyzerConfigDocumentId, SourceText.From("root = false"));
            });

            await VerifySolutionUpdate(workspace, s =>
            {
                return s.RemoveAnalyzerConfigDocument(analyzerConfigDocumentId);
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.RemoteHost)]
        public async Task TestDocument()
        {
            var code = @"class Test { void Method() { } }";

            using var workspace = TestWorkspace.CreateCSharp(code);

            var projectId = workspace.CurrentSolution.ProjectIds.First();
            var documentId = DocumentId.CreateNewId(projectId);
            var documentInfo = DocumentInfo.Create(
                documentId, "sourceFile",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From("class A { }"), VersionStamp.Create())));

            await VerifySolutionUpdate(workspace, s =>
            {
                return s.AddDocument(documentInfo);
            });

            workspace.OnDocumentAdded(documentInfo);

            await VerifySolutionUpdate(workspace, s =>
            {
                return s.WithDocumentText(documentId, SourceText.From("class Changed { }"));
            });

            await VerifySolutionUpdate(workspace, s =>
            {
                return s.RemoveDocument(documentId);
            });
        }

        [Fact]
        public async Task TestRemoteWorkspaceSolutionCrawler()
        {
            var code = @"class Test { void Method() { } }";

            // create base solution
            using var workspace = TestWorkspace.CreateCSharp(code);
            using var remoteWorkspace = CreateRemoteWorkspace();

            // create solution service
            var solution = workspace.CurrentSolution;
            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);

            // update primary workspace
            var solutionChecksum = await solution.State.GetChecksumAsync(CancellationToken.None);
            await remoteWorkspace.UpdatePrimaryBranchSolutionAsync(assetProvider, solutionChecksum, solution.WorkspaceVersion, CancellationToken.None);

            // get solution in remote host
            var remoteSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: false, workspaceVersion: -1, projectId: null, CancellationToken.None);

            // get solution cralwer in remote host
            var solutionCrawlerService = remoteSolution.Workspace.Services.GetService<ISolutionCrawlerRegistrationService>() as SolutionCrawlerRegistrationService;
            Assert.NotNull(solutionCrawlerService);

            // check remote workspace has enabled solution crawler in remote host
            var testAnalyzerProvider = new TestAnalyzerProvider();
            solutionCrawlerService.AddAnalyzerProvider(
                testAnalyzerProvider,
                new IncrementalAnalyzerProviderMetadata("Test", highPriorityForActiveFile: false, workspaceKinds: WorkspaceKind.RemoteWorkspace));

            // check our solution crawler has ran
            Assert.True(await testAnalyzerProvider.Analyzer.Called);

            testAnalyzerProvider.Analyzer.Reset();

            // update remote workspace
            remoteSolution = remoteSolution.WithDocumentText(remoteSolution.Projects.First().Documents.First().Id, SourceText.From(code + " class Test2 { }"));
            remoteWorkspace.UpdateSolutionIfPossible(remoteSolution, solution.WorkspaceVersion + 1);

            // check solution update correctly ran solution crawler
            Assert.True(await testAnalyzerProvider.Analyzer.Called);
        }

        [Fact]
        public async Task TestRemoteWorkspace()
        {
            var code = @"class Test { void Method() { } }";

            // create base solution
            using var workspace = TestWorkspace.CreateCSharp(code);
            using var remoteWorkspace = CreateRemoteWorkspace();

            // create solution service
            var solution1 = workspace.CurrentSolution;
            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution1);

            var remoteSolution1 = await GetInitialOOPSolutionAsync(remoteWorkspace, assetProvider, solution1);

            await Verify(solution1, remoteSolution1, expectRemoteSolutionToCurrent: true);
            var version = solution1.WorkspaceVersion;

            // update remote workspace
            var currentSolution = remoteSolution1.WithDocumentText(remoteSolution1.Projects.First().Documents.First().Id, SourceText.From(code + " class Test2 { }"));
            var oopSolution2 = remoteWorkspace.UpdateSolutionIfPossible(currentSolution, ++version);

            await Verify(currentSolution, oopSolution2, expectRemoteSolutionToCurrent: true);

            // move backward
            await Verify(remoteSolution1, remoteWorkspace.UpdateSolutionIfPossible(remoteSolution1, solution1.WorkspaceVersion), expectRemoteSolutionToCurrent: false);

            // move forward
            currentSolution = oopSolution2.WithDocumentText(oopSolution2.Projects.First().Documents.First().Id, SourceText.From(code + " class Test3 { }"));
            var remoteSolution3 = remoteWorkspace.UpdateSolutionIfPossible(currentSolution, ++version);

            await Verify(currentSolution, remoteSolution3, expectRemoteSolutionToCurrent: true);

            // move to new solution backward
            var (solutionInfo, options) = await assetProvider.CreateSolutionInfoAndOptionsAsync(await solution1.State.GetChecksumAsync(CancellationToken.None), CancellationToken.None);
            Assert.False(remoteWorkspace.TrySetCurrentSolution(solutionInfo, solution1.WorkspaceVersion, options, out var _));

            // move to new solution forward
            Assert.True(remoteWorkspace.TrySetCurrentSolution(solutionInfo, ++version, options, out var newSolution));
            await Verify(solution1, newSolution, expectRemoteSolutionToCurrent: true);

            static async Task<Solution> GetInitialOOPSolutionAsync(RemoteWorkspace remoteWorkspace, AssetProvider assetProvider, Solution solution)
            {
                // set up initial solution
                var solutionChecksum = await solution.State.GetChecksumAsync(CancellationToken.None);
                await remoteWorkspace.UpdatePrimaryBranchSolutionAsync(assetProvider, solutionChecksum, solution.WorkspaceVersion, CancellationToken.None);

                // get solution in remote host
                return await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: false, workspaceVersion: -1, projectId: null, CancellationToken.None);
            }

            static async Task Verify(Solution givenSolution, Solution remoteSolution, bool expectRemoteSolutionToCurrent)
            {
                // verify we got solution expected
                Assert.Equal(await givenSolution.State.GetChecksumAsync(CancellationToken.None), await remoteSolution.State.GetChecksumAsync(CancellationToken.None));

                // verify remote workspace got updated
                Assert.True(expectRemoteSolutionToCurrent == (remoteSolution == remoteSolution.Workspace.CurrentSolution));
            }
        }

        [Theory, CombinatorialData]
        [WorkItem(48564, "https://github.com/dotnet/roslyn/issues/48564")]
        public async Task TestAddingProjectsWithExplicitOptions(bool useDefaultOptionValue)
        {
            using var workspace = TestWorkspace.CreateCSharp(@"public class C { }");
            using var remoteWorkspace = CreateRemoteWorkspace();

            // Initial empty solution
            var solution = workspace.CurrentSolution;
            solution = solution.RemoveProject(solution.ProjectIds.Single());
            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);
            var solutionChecksum = await solution.State.GetChecksumAsync(CancellationToken.None);
            var synched = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: true, workspaceVersion: 0, projectId: null, CancellationToken.None);
            Assert.Equal(solutionChecksum, await synched.State.GetChecksumAsync(CancellationToken.None));

            // Add a C# project and a VB project, set some options, and check again
            var csharpDocument = new TestHostDocument("public class C { }");
            var csharpProject = new TestHostProject(workspace, csharpDocument, language: LanguageNames.CSharp, name: "project2");
            var csharpProjectInfo = csharpProject.ToProjectInfo();

            var vbDocument = new TestHostDocument("Public Class D \r\n  Inherits C\r\nEnd Class");
            var vbProject = new TestHostProject(workspace, vbDocument, language: LanguageNames.VisualBasic, name: "project3");
            var vbProjectInfo = vbProject.ToProjectInfo();

            solution = solution.AddProject(csharpProjectInfo).AddProject(vbProjectInfo);
            var newOptionValue = useDefaultOptionValue
                ? FormattingOptions2.NewLine.DefaultValue
                : FormattingOptions2.NewLine.DefaultValue + FormattingOptions2.NewLine.DefaultValue;
            solution = solution.WithOptions(solution.Options
                .WithChangedOption(FormattingOptions2.NewLine, LanguageNames.CSharp, newOptionValue)
                .WithChangedOption(FormattingOptions2.NewLine, LanguageNames.VisualBasic, newOptionValue));

            assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);
            solutionChecksum = await solution.State.GetChecksumAsync(CancellationToken.None);
            synched = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: true, workspaceVersion: 2, projectId: null, CancellationToken.None);
            Assert.Equal(solutionChecksum, await synched.State.GetChecksumAsync(CancellationToken.None));
        }

        [Fact]
        public async Task TestFrozenSourceGeneratedDocument()
        {
            using var workspace = TestWorkspace.CreateCSharp(@"");
            using var remoteWorkspace = CreateRemoteWorkspace();

            var solution = workspace.CurrentSolution
                .Projects.Single()
                .AddAnalyzerReference(new AnalyzerFileReference(typeof(Microsoft.CodeAnalysis.TestSourceGenerator.HelloWorldGenerator).Assembly.Location, new TestAnalyzerAssemblyLoader()))
                .Solution;

            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);
            var solutionChecksum = await solution.State.GetChecksumAsync(CancellationToken.None);
            var synched = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: true, workspaceVersion: 0, projectId: null, CancellationToken.None);
            Assert.Equal(solutionChecksum, await synched.State.GetChecksumAsync(CancellationToken.None));

            var documentIdentity = (await solution.Projects.Single().GetSourceGeneratedDocumentsAsync()).First().Identity;
            var frozenText = SourceText.From("// Hello, World!");
            solution = solution.WithFrozenSourceGeneratedDocument(documentIdentity, frozenText).Project.Solution;

            assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);
            solutionChecksum = await solution.State.GetChecksumAsync(CancellationToken.None);
            synched = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: false, workspaceVersion: 1, projectId: null, CancellationToken.None);
            Assert.Equal(solutionChecksum, await synched.State.GetChecksumAsync(CancellationToken.None));
        }

        [Fact]
        public async Task TestPartialProjectSync_GetSolutionFirst()
        {
            var code = @"class Test { void Method() { } }";

            using var workspace = TestWorkspace.CreateCSharp(code);
            using var remoteWorkspace = CreateRemoteWorkspace();

            var solution = workspace.CurrentSolution;

            var project1 = solution.Projects.Single();
            var project2 = solution.AddProject("P2", "P2", LanguageNames.CSharp);

            solution = project2.Solution;

            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);

            var solutionChecksum = await solution.State.GetChecksumAsync(CancellationToken.None);
            var syncedFullSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: false, workspaceVersion: -1, projectId: null, CancellationToken.None);

            Assert.Equal(solutionChecksum, await syncedFullSolution.State.GetChecksumAsync(CancellationToken.None));
            Assert.Equal(2, syncedFullSolution.Projects.Count());

            var project1Checksum = await solution.State.GetChecksumAsync(project1.Id, CancellationToken.None);
            var project1SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project1Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project1.Id, CancellationToken.None);
            Assert.Equal(1, project1SyncedSolution.Projects.Count());
            Assert.Equal(project1.Name, project1SyncedSolution.Projects.Single().Name);

            var project2Checksum = await solution.State.GetChecksumAsync(project2.Id, CancellationToken.None);
            var project2SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project2Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(1, project2SyncedSolution.Projects.Count());
            Assert.Equal(project2.Name, project2SyncedSolution.Projects.Single().Name);
        }

        [Fact]
        public async Task TestPartialProjectSync_GetSolutionLast()
        {
            var code = @"class Test { void Method() { } }";

            using var workspace = TestWorkspace.CreateCSharp(code);
            using var remoteWorkspace = CreateRemoteWorkspace();

            var solution = workspace.CurrentSolution;

            var project1 = solution.Projects.Single();
            var project2 = solution.AddProject("P2", "P2", LanguageNames.CSharp);

            solution = project2.Solution;

            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);

            var project1Checksum = await solution.State.GetChecksumAsync(project1.Id, CancellationToken.None);
            var project1SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project1Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project1.Id, CancellationToken.None);
            Assert.Equal(1, project1SyncedSolution.Projects.Count());
            Assert.Equal(project1.Name, project1SyncedSolution.Projects.Single().Name);

            var project2Checksum = await solution.State.GetChecksumAsync(project2.Id, CancellationToken.None);
            var project2SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project2Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(1, project2SyncedSolution.Projects.Count());
            Assert.Equal(project2.Name, project2SyncedSolution.Projects.Single().Name);

            var solutionChecksum = await solution.State.GetChecksumAsync(CancellationToken.None);
            var syncedFullSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: false, workspaceVersion: -1, projectId: null, CancellationToken.None);

            Assert.Equal(solutionChecksum, await syncedFullSolution.State.GetChecksumAsync(CancellationToken.None));
            Assert.Equal(2, syncedFullSolution.Projects.Count());
        }

        [Fact]
        public async Task TestPartialProjectSync_GetDependentProjects1()
        {
            var code = @"class Test { void Method() { } }";

            using var workspace = TestWorkspace.CreateCSharp(code);
            using var remoteWorkspace = CreateRemoteWorkspace();

            var solution = workspace.CurrentSolution;

            var project1 = solution.Projects.Single();
            var project2 = solution.AddProject("P2", "P2", LanguageNames.CSharp);
            var project3 = project2.Solution.AddProject("P3", "P3", LanguageNames.CSharp);

            solution = project3.Solution.AddProjectReference(project3.Id, new(project3.Solution.Projects.Single(p => p.Name == "P2").Id));

            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);

            var project2Checksum = await solution.State.GetChecksumAsync(project2.Id, CancellationToken.None);
            var project2SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project2Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(1, project2SyncedSolution.Projects.Count());
            Assert.Equal(project2.Name, project2SyncedSolution.Projects.Single().Name);

            // syncing project 3 should since project 2 as well because of the p2p ref
            var project3Checksum = await solution.State.GetChecksumAsync(project3.Id, CancellationToken.None);
            var project3SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project3Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(2, project3SyncedSolution.Projects.Count());
        }

        [Fact]
        public async Task TestPartialProjectSync_GetDependentProjects2()
        {
            var code = @"class Test { void Method() { } }";

            using var workspace = TestWorkspace.CreateCSharp(code);
            using var remoteWorkspace = CreateRemoteWorkspace();

            var solution = workspace.CurrentSolution;

            var project1 = solution.Projects.Single();
            var project2 = solution.AddProject("P2", "P2", LanguageNames.CSharp);
            var project3 = project2.Solution.AddProject("P3", "P3", LanguageNames.CSharp);

            solution = project3.Solution.AddProjectReference(project3.Id, new(project3.Solution.Projects.Single(p => p.Name == "P2").Id));

            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);

            // syncing project 3 should since project 2 as well because of the p2p ref
            var project3Checksum = await solution.State.GetChecksumAsync(project3.Id, CancellationToken.None);
            var project3SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project3Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(2, project3SyncedSolution.Projects.Count());

            var project2Checksum = await solution.State.GetChecksumAsync(project2.Id, CancellationToken.None);
            var project2SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project2Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(1, project2SyncedSolution.Projects.Count());
            Assert.Equal(project2.Name, project2SyncedSolution.Projects.Single().Name);

            var project1Checksum = await solution.State.GetChecksumAsync(project1.Id, CancellationToken.None);
            var project1SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project1Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(1, project1SyncedSolution.Projects.Count());
            Assert.Equal(project1.Name, project1SyncedSolution.Projects.Single().Name);
        }

        [Fact]
        public async Task TestPartialProjectSync_GetDependentProjects3()
        {
            var code = @"class Test { void Method() { } }";

            using var workspace = TestWorkspace.CreateCSharp(code);
            using var remoteWorkspace = CreateRemoteWorkspace();

            var solution = workspace.CurrentSolution;

            var project1 = solution.Projects.Single();
            var project2 = solution.AddProject("P2", "P2", LanguageNames.CSharp);
            var project3 = project2.Solution.AddProject("P3", "P3", LanguageNames.CSharp);

            solution = project3.Solution.AddProjectReference(project3.Id, new(project2.Id))
                                        .AddProjectReference(project2.Id, new(project1.Id));

            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);

            // syncing project3 should since project2 and project1 as well because of the p2p ref
            var project3Checksum = await solution.State.GetChecksumAsync(project3.Id, CancellationToken.None);
            var project3SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project3Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(3, project3SyncedSolution.Projects.Count());

            var project2Checksum = await solution.State.GetChecksumAsync(project2.Id, CancellationToken.None);
            var project2SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project2Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(2, project2SyncedSolution.Projects.Count());
            Assert.True(project2SyncedSolution.Projects.Select(p => p.Name).SetEquals(new[] { project2.Name, project1.Name }));

            var project1Checksum = await solution.State.GetChecksumAsync(project1.Id, CancellationToken.None);
            var project1SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project1Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(1, project1SyncedSolution.Projects.Count());
            Assert.Equal(project1.Name, project1SyncedSolution.Projects.Single().Name);
        }

        [Fact]
        public async Task TestPartialProjectSync_GetDependentProjects4()
        {
            var code = @"class Test { void Method() { } }";

            using var workspace = TestWorkspace.CreateCSharp(code);
            using var remoteWorkspace = CreateRemoteWorkspace();

            var solution = workspace.CurrentSolution;

            var project1 = solution.Projects.Single();
            var project2 = solution.AddProject("P2", "P2", LanguageNames.CSharp);
            var project3 = project2.Solution.AddProject("P3", "P3", LanguageNames.CSharp);

            solution = project3.Solution.AddProjectReference(project3.Id, new(project2.Id))
                                        .AddProjectReference(project3.Id, new(project1.Id));

            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);

            // syncing project3 should since project2 and project1 as well because of the p2p ref
            var project3Checksum = await solution.State.GetChecksumAsync(project3.Id, CancellationToken.None);
            var project3SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project3Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(3, project3SyncedSolution.Projects.Count());

            var project2Checksum = await solution.State.GetChecksumAsync(project2.Id, CancellationToken.None);
            var project2SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project2Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(1, project2SyncedSolution.Projects.Count());
            Assert.Equal(project2.Name, project2SyncedSolution.Projects.Single().Name);

            var project1Checksum = await solution.State.GetChecksumAsync(project1.Id, CancellationToken.None);
            var project1SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project1Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(1, project1SyncedSolution.Projects.Count());
            Assert.Equal(project1.Name, project1SyncedSolution.Projects.Single().Name);
        }

        [Fact]
        public async Task TestPartialProjectSync_Options1()
        {
            var code = @"class Test { void Method() { } }";

            using var workspace = TestWorkspace.CreateCSharp(code);
            using var remoteWorkspace = CreateRemoteWorkspace();

            var solution = workspace.CurrentSolution;

            var project1 = solution.Projects.Single();
            var project2 = solution.AddProject("P2", "P2", LanguageNames.VisualBasic);

            solution = project2.Solution;

            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution);

            var solutionChecksum = await solution.State.GetChecksumAsync(CancellationToken.None);
            var syncedFullSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: false, workspaceVersion: -1, projectId: null, CancellationToken.None);

            Assert.Equal(solutionChecksum, await syncedFullSolution.State.GetChecksumAsync(CancellationToken.None));
            Assert.Equal(2, syncedFullSolution.Projects.Count());
            var options = (SerializableOptionSet)syncedFullSolution.Options;
            Assert.Equal(2, options.GetTestAccessor().Languages.Count);

            var project1Checksum = await solution.State.GetChecksumAsync(project1.Id, CancellationToken.None);
            var project1SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project1Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project1.Id, CancellationToken.None);
            Assert.Equal(1, project1SyncedSolution.Projects.Count());
            Assert.Equal(project1.Name, project1SyncedSolution.Projects.Single().Name);
            var project1Options = (SerializableOptionSet)project1SyncedSolution.Options;
            Assert.Equal(2, project1Options.GetTestAccessor().Languages.Count);
            Assert.Contains(LanguageNames.CSharp, project1Options.GetTestAccessor().Languages);

            var project2Checksum = await solution.State.GetChecksumAsync(project2.Id, CancellationToken.None);
            var project2SyncedSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, project2Checksum, fromPrimaryBranch: false, workspaceVersion: -1, project2.Id, CancellationToken.None);
            Assert.Equal(1, project2SyncedSolution.Projects.Count());
            Assert.Equal(project2.Name, project2SyncedSolution.Projects.Single().Name);
            var project2Options = (SerializableOptionSet)project2SyncedSolution.Options;
            Assert.Equal(2, project2Options.GetTestAccessor().Languages.Count);
            Assert.Contains(LanguageNames.VisualBasic, project2Options.GetTestAccessor().Languages);
        }

        private static async Task VerifySolutionUpdate(string code, Func<Solution, Solution> newSolutionGetter)
        {
            using var workspace = TestWorkspace.CreateCSharp(code);
            await VerifySolutionUpdate(workspace, newSolutionGetter);
        }

        private static async Task VerifySolutionUpdate(
            TestWorkspace workspace,
            Func<Solution, Solution> newSolutionGetter,
            Action<Solution> oldSolutionValidator = null,
            Action<Solution> newSolutionValidator = null)
        {
            var solution = workspace.CurrentSolution;
            oldSolutionValidator?.Invoke(solution);

            var map = new Dictionary<Checksum, object>();

            using var remoteWorkspace = CreateRemoteWorkspace();
            var assetProvider = await GetAssetProviderAsync(workspace, remoteWorkspace, solution, map);
            var solutionChecksum = await solution.State.GetChecksumAsync(CancellationToken.None);

            // update primary workspace
            await remoteWorkspace.UpdatePrimaryBranchSolutionAsync(assetProvider, solutionChecksum, solution.WorkspaceVersion, CancellationToken.None);
            var recoveredSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, solutionChecksum, fromPrimaryBranch: false, workspaceVersion: -1, projectId: null, CancellationToken.None);
            oldSolutionValidator?.Invoke(recoveredSolution);

            Assert.IsAssignableFrom<RemoteWorkspace>(recoveredSolution.Workspace);
            var primaryWorkspace = recoveredSolution.Workspace;
            Assert.Equal(solutionChecksum, await recoveredSolution.State.GetChecksumAsync(CancellationToken.None));

            // get new solution
            var newSolution = newSolutionGetter(solution);
            var newSolutionChecksum = await newSolution.State.GetChecksumAsync(CancellationToken.None);
            await newSolution.AppendAssetMapAsync(includeProjectCones: true, map, CancellationToken.None);

            // get solution without updating primary workspace
            var recoveredNewSolution = await remoteWorkspace.GetSolutionAsync(assetProvider, newSolutionChecksum, fromPrimaryBranch: false, workspaceVersion: -1, projectId: null, CancellationToken.None);

            Assert.Equal(newSolutionChecksum, await recoveredNewSolution.State.GetChecksumAsync(CancellationToken.None));

            // do same once updating primary workspace
            await remoteWorkspace.UpdatePrimaryBranchSolutionAsync(assetProvider, newSolutionChecksum, solution.WorkspaceVersion + 1, CancellationToken.None);
            var third = await remoteWorkspace.GetSolutionAsync(assetProvider, newSolutionChecksum, fromPrimaryBranch: false, workspaceVersion: -1, projectId: null, CancellationToken.None);

            Assert.Equal(newSolutionChecksum, await third.State.GetChecksumAsync(CancellationToken.None));

            newSolutionValidator?.Invoke(recoveredNewSolution);
        }

        private static async Task<AssetProvider> GetAssetProviderAsync(Workspace workspace, RemoteWorkspace remoteWorkspace, Solution solution, Dictionary<Checksum, object> map = null)
        {
            // make sure checksum is calculated
            await solution.State.GetChecksumAsync(CancellationToken.None);

            map ??= new Dictionary<Checksum, object>();
            await solution.AppendAssetMapAsync(includeProjectCones: true, map, CancellationToken.None);

            var sessionId = 0;
            var storage = new SolutionAssetCache();
            var assetSource = new SimpleAssetSource(workspace.Services.GetService<ISerializerService>(), map);

            return new AssetProvider(sessionId, storage, assetSource, remoteWorkspace.Services.GetService<ISerializerService>());
        }

        private class TestAnalyzerProvider : IIncrementalAnalyzerProvider
        {
            public readonly TestAnalyzer Analyzer = new TestAnalyzer();

            public IIncrementalAnalyzer CreateIncrementalAnalyzer(Workspace workspace)
            {
                return Analyzer;
            }

            public class TestAnalyzer : IncrementalAnalyzerBase
            {
                private TaskCompletionSource<bool> _source = new TaskCompletionSource<bool>();

                public override Task AnalyzeDocumentAsync(Document document, SyntaxNode bodyOpt, InvocationReasons reasons, CancellationToken cancellationToken)
                {
                    _source.SetResult(true);
                    return Task.CompletedTask;
                }

                public Task<bool> Called => _source.Task;

                public void Reset()
                {
                    _source = new TaskCompletionSource<bool>();
                }
            }
        }
    }
}
