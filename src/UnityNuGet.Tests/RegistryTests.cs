﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Moq;
using NuGet.Configuration;
using NuGet.PackageManagement;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using NUnit.Framework;
using static NuGet.Frameworks.FrameworkConstants;

namespace UnityNuGet.Tests
{
    public class RegistryTests
    {
        private static readonly RegistryOptions s_registryOptions = new() { RegistryFilePath = "registry.json" };

        [Test]
        [TestCase("scriban")]
        [TestCase("Scriban")]
        public async Task Make_Sure_That_The_Registry_Is_Case_Insensitive(string packageName)
        {
            var hostEnvironmentMock = new Mock<IHostEnvironment>();
            hostEnvironmentMock.Setup(h => h.EnvironmentName).Returns(Environments.Development);

            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(new FakeLoggerProvider());

            var registry = new Registry(hostEnvironmentMock.Object, loggerFactory, Options.Create(s_registryOptions));

            await registry.StartAsync(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(registry.TryGetValue(packageName, out RegistryEntry? result), Is.True);
                Assert.That(result, Is.Not.Null);
            });
        }

        [Test]
        public async Task Make_Sure_That_The_Order_In_The_Registry_Is_Respected()
        {
            var hostEnvironmentMock = new Mock<IHostEnvironment>();
            hostEnvironmentMock.Setup(h => h.EnvironmentName).Returns(Environments.Development);

            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(new FakeLoggerProvider());

            var registry = new Registry(hostEnvironmentMock.Object, loggerFactory, Options.Create(s_registryOptions));

            await registry.StartAsync(CancellationToken.None);

            string[] originalPackageNames = registry.Select(r => r.Key).ToArray();
            string[] sortedPackageNames = [.. originalPackageNames.OrderBy(p => p)];

            Assert.That(originalPackageNames, Is.EqualTo(sortedPackageNames));
        }

        [Test]
        public async Task Ensure_That_Packages_Already_Included_In_Net_Standard_Are_not_Included_In_The_Registry()
        {
            var hostEnvironmentMock = new Mock<IHostEnvironment>();
            hostEnvironmentMock.Setup(h => h.EnvironmentName).Returns(Environments.Development);

            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(new FakeLoggerProvider());

            var registry = new Registry(hostEnvironmentMock.Object, loggerFactory, Options.Create(s_registryOptions));

            await registry.StartAsync(CancellationToken.None);

            string[] packageNames = registry.Select(r => r.Key).Where(DotNetHelper.IsNetStandard20Assembly).ToArray();

            Assert.That(packageNames, Is.Empty);
        }

        [Test]
        public async Task CanParse_PackageWithRuntimes()
        {
            var logger = new NuGetConsoleTestLogger();
            CancellationToken cancellationToken = CancellationToken.None;

            var cache = new SourceCacheContext();
            ISettings settings = Settings.LoadDefaultSettings(root: null);
            SourceRepository repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");

            // Fetch a package that has runtime overrides as described here: https://learn.microsoft.com/en-us/nuget/create-packages/supporting-multiple-target-frameworks
            DownloadResourceResult downloadResult = await PackageDownloader.GetDownloadResourceResultAsync(
                    [repository],
                    new PackageIdentity("System.Security.Cryptography.ProtectedData", new NuGetVersion(6, 0, 0)),
                    new PackageDownloadContext(cache),
                    SettingsUtility.GetGlobalPackagesFolder(settings),
                    logger, cancellationToken);

            // Make sure we have runtime libraries
            List<(string file, UnityOs, UnityCpu?)> runtimeLibs = await RuntimeLibraries
                .GetSupportedRuntimeLibsAsync(downloadResult.PackageReader, CommonFrameworks.NetStandard20, logger)
                .ToListAsync();
            Assert.That(runtimeLibs, Is.Not.Empty);

            // Make sure these runtime libraries are only for Windows
            var platformDefs = PlatformDefinition.CreateAllPlatforms();
            PlatformDefinition? win = platformDefs.Find(UnityOs.Windows);
            foreach ((string file, UnityOs os, UnityCpu? cpu) in runtimeLibs)
            {
                Assert.That(platformDefs.Find(os, cpu), Is.EqualTo(win));
            }

            // Get the lib files
            IEnumerable<FrameworkSpecificGroup> versions = await downloadResult.PackageReader.GetLibItemsAsync(cancellationToken);
            IEnumerable<(FrameworkSpecificGroup, RegistryTargetFramework)> closestVersions = NuGetHelper.GetClosestFrameworkSpecificGroups(
                versions,
                [
                    new()
                    {
                        Framework = CommonFrameworks.NetStandard20,
                    },
                ]);
            var libFiles = closestVersions
                .Single()
                .Item1.Items
                .Select(i => Path.GetFileName(i))
                .ToHashSet();

            // Make sure the runtime files fully replace the lib files (note that this is generally not a requirement)
            var runtimeFiles = runtimeLibs
                .Select(l => Path.GetFileName(l.file))
                .ToHashSet();
            Assert.That(libFiles.SetEquals(runtimeFiles), Is.True);
        }

        static async Task<TestCaseData[]> AllRegistries()
        {
            var hostEnvironmentMock = new Mock<IHostEnvironment>();
            hostEnvironmentMock.Setup(h => h.EnvironmentName).Returns(Environments.Development);

            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(new FakeLoggerProvider());

            var registry = new Registry(hostEnvironmentMock.Object, loggerFactory, Options.Create(s_registryOptions));

            var logger = new NuGetConsoleTestLogger();
            CancellationToken cancellationToken = CancellationToken.None;

            await registry.StartAsync(cancellationToken);

            var cache = new SourceCacheContext();
            SourceRepository repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            PackageMetadataResource resource = await repository.GetResourceAsync<PackageMetadataResource>();

            var nuGetFrameworks = new RegistryTargetFramework[] { new() { Framework = CommonFrameworks.NetStandard20 } };

            string[] excludedPackages = [
                // All versions target "Any" and not .netstandard2.0 / 2.1
                // It has too many versions, the minimum version is lifted so as not to process so many versions
                @"AWSSDK.*",
                // It has too many versions, the minimum version is lifted so as not to process so many versions
                @"CSharpFunctionalExtensions",
                // Some versions between 5.6.4 and 6.3.0 doesn't ship .netstandard2.0.
                @"Elasticsearch.Net",
                // It has too many versions, the minimum version is lifted so as not to process so many versions
                @"Google.Apis.AndroidPublisher.v3",
                // Versions prior to 1.11.24 depend on System.Xml.XPath.XmlDocument which does not target .netstandard2.0
                @"HtmlAgilityPack",
                // Although 2.x targets .netstandard2.0 it has an abandoned dependency (Remotion.Linq) that does not target .netstandard2.0.
                // 3.1.0 is set because 3.0.x only targets .netstandard2.1.
                @"Microsoft.EntityFrameworkCore.*",
                // Monomod Versions < 18.11.9.9 depend on System.Runtime.Loader which doesn't ship .netstandard2.0.
                @"MonoMod.Utils",
                @"MonoMod.RuntimeDetour",
                // Versions < 2.0.0 depend on NAudio which doesn't ship .netstandard2.0.
                @"MumbleSharp",
                // Versions < 3.2.1 depend on Nullable which doesn't ship .netstandard2.0.
                @"Serilog.Expressions",
                // Versions < 1.4.1 has dependencies on Microsoft.AspNetCore.*.
                @"StrongInject.Extensions.DependencyInjection",
                // Versions < 4.6.0 in theory supports .netstandard2.0 but it doesn't have a lib folder with assemblies and it makes it fail.
                @"System.Private.ServiceModel",
                // Versions < 0.8.6 depend on LiteGuard, a deprecated dependency.
                @"Telnet",
                // Version < 1.0.26 depends on Microsoft.Windows.Compatibility, this one has tons of dependencies that don't target .netstandard2.0. And one of them is System.Speech that doesn't work in Unity.
                @"Dapplo.Windows.Common",
                @"Dapplo.Windows.Input",
                @"Dapplo.Windows.Messages",
                @"Dapplo.Windows.User32",
                // It has too many versions, the minimum version is lifted so as not to process so many versions
                @"UnitsNet.*",
                // Most versions < 1.7.0 don't target .netstandard2.0
                @"XLParser",
                // Versions < 1.3.1 has dependencies on PolySharp
                @"Utf8StringInterpolation",
                // Versions 2.0.0 has dependencies on Utf8StringInterpolation 1.3.0
                @"ZLogger",
                // Version 3.1.8 has dependency on `Panic.StringUtils` which doesn't support .netstandard2.0 or 2.1. Rest of versions are fine.
                @"GraphQL.Client.Serializer.Newtonsoft",
                // Version 3.1.8 has dependency on `Panic.StringUtils` which doesn't support .netstandard2.0 or 2.1. Rest of versions are fine.
                @"GraphQL.Client.Serializer.SystemTextJson"
            ];

            var excludedPackagesRegex = new Regex(@$"^{string.Join('|', excludedPackages)}$");

            return registry.Where(r => !r.Value.Analyzer && !r.Value.Ignored).OrderBy((pair) => pair.Key).Select((pair) =>
            {
                return new TestCaseData(
                    resource,
                    logger,
                    cache,
                    repository,
                    excludedPackagesRegex,
                    nuGetFrameworks,
                    pair.Key,
                    pair.Value.IncludePrerelease,
                    pair.Value.IncludeUnlisted,
                    pair.Value.Version).SetArgDisplayNames(pair.Key, pair.Value.Version!.ToString());
            }).ToArray();
        }

        const int MaxAllowedVersions = 100;

        [TestCaseSource(nameof(AllRegistries))]
        public async Task Ensure_Min_Version_Is_Correct_Ignoring_Analyzers_And_Native_Libs(PackageMetadataResource resource,
            NuGetConsoleTestLogger logger,
            SourceCacheContext cache,
            SourceRepository repository,
            Regex excludedPackagesRegex,
            RegistryTargetFramework[] nuGetFrameworks,
            string packageId,
            bool includePrerelease,
            bool includeUnlisted,
            VersionRange versionRange)
        {
            IEnumerable<IPackageSearchMetadata> dependencyPackageMetas = await resource.GetMetadataAsync(
                packageId,
                includePrerelease,
                includeUnlisted,
                cache,
                logger,
                CancellationToken.None);

            IPackageSearchMetadata[] versions = dependencyPackageMetas.Where(v => versionRange!.Satisfies(v.Identity.Version)).ToArray();
            Warn.If(versions, Has.Length.GreaterThan(MaxAllowedVersions));

            if (excludedPackagesRegex.IsMatch(packageId))
                return;

            PackageIdentity? packageIdentity = NuGetHelper.GetMinimumCompatiblePackageIdentity(dependencyPackageMetas, nuGetFrameworks, includeAny: false);

            if (packageIdentity != null)
            {
                Assert.That(versionRange!.MinVersion, Is.EqualTo(packageIdentity.Version), $"Package {packageId}");
            }
            else
            {
                ISettings settings = Settings.LoadDefaultSettings(root: null);

                DownloadResourceResult downloadResult = await PackageDownloader.GetDownloadResourceResultAsync(
                        [repository],
                        new PackageIdentity(packageId, versionRange!.MinVersion),
                        new PackageDownloadContext(cache),
                        SettingsUtility.GetGlobalPackagesFolder(settings),
                        logger, CancellationToken.None);

                bool hasNativeLib = await NativeLibraries.GetSupportedNativeLibsAsync(downloadResult.PackageReader, logger).AnyAsync();
                Assert.That(hasNativeLib, packageId);
            }
        }
    }
}
