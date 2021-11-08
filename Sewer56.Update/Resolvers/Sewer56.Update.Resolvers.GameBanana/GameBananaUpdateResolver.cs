﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Versioning;
using Sewer56.Update.Extensions;
using Sewer56.Update.Interfaces;
using Sewer56.Update.Misc;
using Sewer56.Update.Packaging.Interfaces;
using Sewer56.Update.Packaging.Structures;
using Sewer56.Update.Resolvers.GameBanana.Structures;
using Sewer56.Update.Structures;

namespace Sewer56.Update.Resolvers.GameBanana;

/// <summary>
/// A package resolver that allows people to receive updates performed via gamebanana.
/// </summary>
public class GameBananaUpdateResolver : IPackageResolver
{
    private GameBananaResolverConfiguration _configuration;
    private CommonPackageResolverSettings _commonResolverSettings;

    private ReleaseMetadata? _releases;
    private GameBananaItem? _gbItem;

    /// <summary>
    /// Creates a new instance of the GameBanana update resolver.
    /// </summary>
    /// <param name="configuration">Configuration specific to GameBanana.</param>
    /// <param name="commonResolverSettings">Configuration settings shared between all items.</param>
    public GameBananaUpdateResolver(GameBananaResolverConfiguration configuration, CommonPackageResolverSettings? commonResolverSettings = null)
    {
        _configuration = configuration;
        _commonResolverSettings = commonResolverSettings ?? new CommonPackageResolverSettings();
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _gbItem   = await GameBananaItem.FromTypeAndIdAsync(_configuration.ModType, _configuration.ItemId);
        if (_gbItem == null)
            return;

        // Download Metadata
        var metadataFile = GetGameBananaMetadataFile(_gbItem.Files, out var isZip);
        if (metadataFile == null!)
            return;

        using var client = new WebClient();
        var bytes = await client.DownloadDataTaskAsync(metadataFile.DownloadUrl);
        if (isZip)
        {
            await using var memoryStream    = new MemoryStream(bytes);
            using var zipFile               = new ZipArchive(memoryStream);
            await using var metadataStream  = zipFile.GetEntry(_commonResolverSettings.MetadataFileName)!.Open();
            _releases = await Singleton<ReleaseMetadata>.Instance.ReadFromStreamAsync(metadataStream);
            return;
        }

        _releases = Singleton<ReleaseMetadata>.Instance.ReadFromData(bytes);
    }

    /// <inheritdoc />
    public Task<List<NuGetVersion>> GetPackageVersionsAsync(CancellationToken cancellationToken = default)
    {
        if (_releases == null)
            return Task.FromResult(new List<NuGetVersion>());
        
        return Task.FromResult(_releases!.GetNuGetVersionsFromReleaseMetadata(_commonResolverSettings.AllowPrereleases));
    }

    /// <inheritdoc />
    public async Task DownloadPackageAsync(NuGetVersion version, string destFilePath, ReleaseMetadataVerificationInfo verificationInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_releases == null)
            return;

        var releaseItem      = _releases.GetRelease(version.ToString(), verificationInfo);
        var expectedFileName = GameBananaUtilities.GetFileNameStart(releaseItem.FileName);
        var gbItemFile       = _gbItem.Files.FirstOrDefault(x => x.Value.FileName.StartsWith(expectedFileName, StringComparison.OrdinalIgnoreCase)).Value;

        //Create a WebRequest to get the file & create a response. 
        var fileReq  = WebRequest.CreateHttp(gbItemFile.DownloadUrl);
        var fileResp = await fileReq.GetResponseAsync();
        await using var responseStream = fileResp.GetResponseStream();
        await using var targetFile = File.Open(destFilePath, System.IO.FileMode.Create);
        await responseStream.CopyToAsyncEx(targetFile, 262144, progress, cancellationToken);
    }

    private GameBananaItemFile? GetGameBananaMetadataFile(Dictionary<string, GameBananaItemFile> files, out bool isZip)
    {
        var expectedFileName = GameBananaUtilities.GetFileNameStart(_commonResolverSettings.MetadataFileName);
        foreach (var file in files)
        {
            if (!file.Value.FileName.StartsWith(expectedFileName))
                continue;

            isZip = Path.GetExtension(file.Value.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase);
            return file.Value;
        }

        isZip = false;
        return null;
    }
}