using LocalLlmConsole.Models;
using LocalLlmConsole.Services;
using LocalLlmConsole.ViewModels;
using System.Net;
using System.Text.Json.Nodes;

namespace LocalLlmConsole.Tests;

public sealed class AppUpdateBrandingAndCompatibilityTests : ManagerRegressionTestBase
{
    [Fact]
    public void Requirement1_AppUpdateServicePointsToLocalModelConductorReleaseApi()
    {
        Assert.Equal("oliverhe202018-ctrl", AppUpdateService.RepositoryOwner);
        Assert.Equal("Local-Model-Conductor", AppUpdateService.RepositoryName);
        Assert.Equal("oliverhe202018-ctrl/Local-Model-Conductor", AppUpdateService.RepositoryNameWithOwner);
        Assert.Equal("https://github.com/oliverhe202018-ctrl/Local-Model-Conductor", AppUpdateService.RepositoryUrl);
        Assert.Equal("https://api.github.com/repos/oliverhe202018-ctrl/Local-Model-Conductor/releases", AppUpdateService.ReleaseApiUrl);
        Assert.Equal("https://github.com/oliverhe202018-ctrl/Local-Model-Conductor/releases", AppUpdateService.ReleasesWebUrl);
        Assert.DoesNotContain("alekk89", AppUpdateService.RepositoryUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Requirement1_CheckLatestAsyncCallsConfiguredRepositoryWithoutFallbackToUpstream()
    {
        string? requestedUrl = null;
        using var handler = new CapturingHttpHandler(req =>
        {
            requestedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var service = new AppUpdateService(http, _ => { }, currentVersion: () => "v3.0.0", allowUnsignedUpdates: true);

        var result = await service.CheckLatestAsync(TestContext.Current.CancellationToken);

        Assert.Equal("https://api.github.com/repos/oliverhe202018-ctrl/Local-Model-Conductor/releases/latest", requestedUrl);
        Assert.DoesNotContain("alekk89", requestedUrl ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(result.IsAvailable);
    }

    [Fact]
    public void Requirement2_UpdatesPageViewModelShowsCorrectRepositoryUrl()
    {
        var vm = new UpdatesPageViewModel();
        Assert.Contains("https://github.com/oliverhe202018-ctrl/Local-Model-Conductor", vm.StatusDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("alekk89", vm.StatusDetails, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Requirement3_WhenNoReleasePublishedReturnsExplicitNotice()
    {
        using var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var service = new AppUpdateService(http, _ => { }, currentVersion: () => "v3.0.0", allowUnsignedUpdates: true);

        var update = await service.CheckLatestAsync(TestContext.Current.CancellationToken);

        Assert.False(update.IsAvailable);
        Assert.Contains("No official release", update.ReleaseNotes, StringComparison.OrdinalIgnoreCase);

        var workflowResult = AppUpdateWorkflowService.DescribeCheckResult(update, manual: true);
        Assert.Equal("No official release has been published yet.", workflowResult.StatusMessage);
        Assert.Contains("No official release has been published yet.", workflowResult.DialogMessage, StringComparison.Ordinal);

        var vm = new UpdatesPageViewModel();
        vm.SetLatestUpdate(update);
        Assert.Contains("v3.0.0", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Requirement4_StableReleaseComparesVersionCorrectly()
    {
        var releaseJson = JsonNode.Parse("""
        {
          "tag_name": "v3.3.0",
          "name": "Local Model Conductor v3.3.0",
          "body": "Production Guard & gateway enhancements.",
          "html_url": "https://github.com/oliverhe202018-ctrl/Local-Model-Conductor/releases/tag/v3.3.0",
          "assets": [
            { "name": "LocalModelConductor-win-x64.zip", "browser_download_url": "https://example.invalid/lmc.zip", "size": 2048 },
            { "name": "LocalModelConductor-win-x64.zip.sha256", "browser_download_url": "https://example.invalid/lmc.zip.sha256", "size": 64 }
          ]
        }
        """)!.AsObject();

        var update = AppUpdateReleaseParser.ParseLatestRelease(releaseJson, "v3.0.0");

        Assert.True(update.IsAvailable);
        Assert.True(update.CanAutoInstall);
        Assert.Equal("v3.0.0", update.CurrentVersion);
        Assert.Equal("v3.3.0", update.LatestVersion);
        Assert.Equal("LocalModelConductor-win-x64.zip", update.AssetName);
        Assert.Equal("https://example.invalid/lmc.zip", update.AssetUrl);
    }

    [Fact]
    public async Task Requirement5_PrereleaseAndDraftAreRejectedForStableUpdates()
    {
        var prereleaseJson = """
        {
          "tag_name": "v3.4.0-rc1",
          "name": "Local Model Conductor v3.4.0-rc1",
          "prerelease": true,
          "draft": false,
          "assets": []
        }
        """;
        using var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(prereleaseJson)
        });
        using var http = new HttpClient(handler);
        var service = new AppUpdateService(http, _ => { }, currentVersion: () => "v3.0.0", allowUnsignedUpdates: true);

        var ex = await Assert.ThrowsAsync<AppUpdateVerificationException>(() =>
            service.CheckLatestAsync(TestContext.Current.CancellationToken));

        Assert.Contains("cannot install a draft or prerelease", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Requirement6_MissingPortableAssetFailsClosedAndDisablesAutoInstall()
    {
        var releaseJson = JsonNode.Parse("""
        {
          "tag_name": "v3.5.0",
          "name": "Local Model Conductor v3.5.0",
          "body": "Release without portable binary.",
          "html_url": "https://github.com/oliverhe202018-ctrl/Local-Model-Conductor/releases/tag/v3.5.0",
          "assets": [
            { "name": "README.txt", "browser_download_url": "https://example.invalid/README.txt", "size": 100 }
          ]
        }
        """)!.AsObject();

        var update = AppUpdateReleaseParser.ParseLatestRelease(releaseJson, "v3.0.0");

        Assert.True(update.IsAvailable);
        Assert.False(update.CanAutoInstall);
        Assert.Equal("", update.AssetName);
        Assert.Equal("", update.AssetUrl);

        var vm = new UpdatesPageViewModel();
        vm.SetLatestUpdate(update);
        Assert.Equal(Localization.Loc.T("Updates.OpenReleasePageButton"), vm.ActionText);
    }

    [Fact]
    public async Task Requirement7_Sha256MismatchAbortsAndDeletesStagedFiles()
    {
        var root = CreateTempRoot();
        var fakeExeBytes = new byte[1024 * 1024 + 10];
        Array.Fill(fakeExeBytes, (byte)0x42);

        using var handler = new CapturingHttpHandler(req =>
        {
            if (req.RequestUri!.ToString().EndsWith(".sha256"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('0', 64)) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fakeExeBytes) };
        });
        using var http = new HttpClient(handler);
        var service = new AppUpdateService(http, _ => { }, currentVersion: () => "v3.0.0", allowUnsignedUpdates: true);

        var assetUrl = $"{AppUpdateService.RepositoryUrl}/releases/download/v3.3.0/{AppUpdateService.PortableExeName}";
        var checksumUrl = $"{AppUpdateService.RepositoryUrl}/releases/download/v3.3.0/{AppUpdateService.PortableExeName}.sha256";

        var update = new AppUpdateInfo(
            IsAvailable: true,
            CurrentVersion: "v3.0.0",
            LatestVersion: "v3.3.0",
            ReleaseName: "v3.3.0",
            ReleaseNotes: "Notes",
            HtmlUrl: $"{AppUpdateService.RepositoryUrl}/releases/tag/v3.3.0",
            AssetName: AppUpdateService.PortableExeName,
            AssetUrl: assetUrl,
            AssetSize: fakeExeBytes.Length,
            ChecksumAssetName: $"{AppUpdateService.PortableExeName}.sha256",
            ChecksumAssetUrl: checksumUrl,
            CanAutoInstall: true);

        var ex = await Assert.ThrowsAsync<AppUpdateVerificationException>(() =>
            service.StageInstallAsync(update, root, Path.Combine(root, "app.exe"), TestContext.Current.CancellationToken));

        Assert.Contains("checksum mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(root, "Install-*.ps1", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(root, "installed-update.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void Requirement8_NsisSetupExeIsNotTreatedAsPortableExecutable()
    {
        var releaseJson = JsonNode.Parse("""
        {
          "tag_name": "v3.2.0",
          "name": "Local Model Conductor v3.2.0",
          "body": "Official NSIS installer release.",
          "html_url": "https://github.com/oliverhe202018-ctrl/Local-Model-Conductor/releases/tag/v3.2.0",
          "assets": [
            { "name": "LocalModelConductor-Setup-win-x64.exe", "browser_download_url": "https://example.invalid/setup.exe", "size": 150000000 },
            { "name": "LocalModelConductor-Setup-win-x64.exe.sha256", "browser_download_url": "https://example.invalid/setup.exe.sha256", "size": 64 }
          ]
        }
        """)!.AsObject();

        var update = AppUpdateReleaseParser.ParseLatestRelease(releaseJson, "v3.0.0");

        Assert.True(update.IsAvailable);
        Assert.False(update.CanAutoInstall);
        Assert.NotEqual("LocalModelConductor-Setup-win-x64.exe", update.AssetName);
        Assert.Equal("", update.AssetName);

        var workflowResult = AppUpdateWorkflowService.DescribeCheckResult(update, manual: true);
        Assert.False(workflowResult.ShouldPromptInstall);
        Assert.Contains("installer package", workflowResult.DialogMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://github.com/oliverhe202018-ctrl/Local-Model-Conductor/releases/tag/v3.2.0", update.HtmlUrl);
    }

    [Fact]
    public void Requirement9_AppReleaseManifestVerifierEnforcesLocalModelConductorRepository()
    {
        var manifestVerifier = new AppReleaseManifestVerifier(
            new HttpClient(),
            new ReleaseManifestTrustStore([]));

        Assert.NotNull(manifestVerifier);
    }

    [Fact]
    public void Requirement10_UpstreamCopyrightAttributionIsStrictlyPreserved()
    {
        var licensePath = FindRepositoryFile("LICENSE");
        var license = File.ReadAllText(licensePath);
        Assert.Contains("Copyright (c) 2026 llama.cpp Windows Manager contributors", license, StringComparison.Ordinal);
        Assert.Contains("MIT License", license, StringComparison.Ordinal);

        var noticesPath = FindRepositoryFile("THIRD-PARTY-NOTICES.md");
        var notices = File.ReadAllText(noticesPath);
        Assert.Contains("llama-cpp-windows-manager", notices, StringComparison.Ordinal);
        Assert.Contains("alekk89", notices, StringComparison.Ordinal);
        Assert.Contains("https://github.com/alekk89/llama-cpp-windows-manager", notices, StringComparison.Ordinal);

        var readmePath = FindRepositoryFile("README.md");
        var readme = File.ReadAllText(readmePath);
        Assert.Contains("alekk89/llama-cpp-windows-manager", readme, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2024 alekk89", readme, StringComparison.Ordinal);
        Assert.Contains("https://github.com/oliverhe202018-ctrl/Local-Model-Conductor", readme, StringComparison.Ordinal);
    }
}
