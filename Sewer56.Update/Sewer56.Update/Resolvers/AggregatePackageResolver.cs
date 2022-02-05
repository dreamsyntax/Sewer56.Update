﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Versioning;
using Sewer56.Update.Interfaces;
using Sewer56.Update.Interfaces.Extensions;
using Sewer56.Update.Packaging.Structures;

namespace Sewer56.Update.Resolvers;

/// <summary>
/// A package resolver that supports downloading of packages from multiple sources.
/// </summary>
public class AggregatePackageResolver : IPackageResolver, IPackageResolverDownloadSize
{
    /// <summary>
    /// Number of resolvers internally in this aggregate resolver.
    /// </summary>
    public int Count => _resolverItems.Length;

    private AggregatePackageResolverItem[] _resolverItems;
    private bool _hasAcquiredPackages;
    private bool _hasInitialised;

    /// <summary/>
    /// <param name="resolvers">A list of existing resolvers.</param>
    public AggregatePackageResolver(List<IPackageResolver> resolvers)
    {
        _resolverItems = new AggregatePackageResolverItem[resolvers.Count];
        for (int x = 0; x < _resolverItems.Length; x++)
            _resolverItems[x].Resolver = resolvers[x];
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (_hasInitialised)
            return;

        _hasInitialised = true;
        var tasks = new Task[_resolverItems.Length];
        for (var x = 0; x < _resolverItems.Length; x++)
        {
            var resolver = _resolverItems[x];
            tasks[x] = resolver.Resolver.InitializeAsync();
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            ThrowOnAllTasksFaulted(tasks);
        }
    }

    /// <inheritdoc />
    public async Task<List<NuGetVersion>> GetPackageVersionsAsync(CancellationToken cancellationToken = default)
    {
        await AcquirePackagesIfNecessaryAsync(cancellationToken);
        return FlattenVersions();
    }

    /// <inheritdoc />
    public async Task DownloadPackageAsync(NuGetVersion version, string destFilePath, ReleaseMetadataVerificationInfo verificationInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var resolver = (await GetResolverForVersionAsync(version, cancellationToken)).Resolver;
        await resolver.DownloadPackageAsync(version, destFilePath, verificationInfo, progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<long> GetDownloadFileSizeAsync(NuGetVersion version, ReleaseMetadataVerificationInfo verificationInfo, CancellationToken token = default)
    {
        var resolver = (await GetResolverForVersionAsync(version, token)).Resolver;
        if (resolver is IPackageResolverDownloadSize downloadSizeProvider)
            return await downloadSizeProvider.GetDownloadFileSizeAsync(version, verificationInfo, token);
            
        return -1;
    }

    /// <summary>
    /// Returns the update resolver that would be used to update to the given version.
    /// </summary>
    /// <param name="version">The version to be used for updating.</param>
    /// <param name="token">Token used to cancel the operation used to acquire packages (if first use).</param>
    /// <returns>Details of the resolver used for updating.</returns>
    public async Task<GetResolverResult> GetResolverForVersionAsync(NuGetVersion version, CancellationToken token)
    {
        await AcquirePackagesIfNecessaryAsync(token);
        for (var x = 0; x < _resolverItems.Length; x++)
        {
            var resolver = _resolverItems[x];
            if (resolver.Versions.Find(x => x.Equals(version)) != default)
                return new GetResolverResult(resolver.Resolver, x);
        }

        throw new Exception("Resolver with a specified version was not found.");
    }

    private List<NuGetVersion> FlattenVersions()
    {
        var hashSet = new HashSet<NuGetVersion>();
        foreach (var resolver in _resolverItems)
        foreach (var version in resolver.Versions)
            hashSet.Add(version);

        var result = hashSet.ToList();
        result.Sort((a, b) => a.CompareTo(b));
        return result;
    }

    private async Task AcquirePackagesIfNecessaryAsync(CancellationToken token)
    {
        if (_hasAcquiredPackages)
            return;

        var tasks = new Task<List<NuGetVersion>>[_resolverItems.Length];
        for (var x = 0; x < _resolverItems.Length; x++)
            tasks[x] = _resolverItems[x].Resolver.GetPackageVersionsAsync(token);

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            ThrowOnAllTasksFaulted(tasks);
        }

        for (int x = 0; x < _resolverItems.Length; x++)
            _resolverItems[x].Versions = !tasks[x].IsFaulted ? tasks[x].Result : new List<NuGetVersion>();

        _hasAcquiredPackages = true;
    }
    private static void ThrowOnAllTasksFaulted(Task[] tasks)
    {
        if (tasks.All(x => x.IsFaulted))
            throw new AggregateException(tasks.Select(x => x.Exception)!);
    }

    private static void ThrowOnAllTasksFaulted<T>(Task<T>[] tasks)
    {
        if (tasks.All(x => x.IsFaulted))
            throw new AggregateException(tasks.Select(x => x.Exception)!);
    }

    /// <summary>
    /// Encapsulates the result of searching for a resolver supporting a given package version.
    /// </summary>
    public struct GetResolverResult
    {
        /// <summary/>
        public GetResolverResult(IPackageResolver resolver, int index)
        {
            Resolver = resolver;
            Index = index;
        }

        /// <summary>
        /// The resolver to be used for updating.
        /// </summary>
        public IPackageResolver Resolver { get; internal set; }

        /// <summary>
        /// Index of the resolver in the originally supplied array of resolvers.
        /// </summary>
        public int Index { get; internal set; }
    }

    private struct AggregatePackageResolverItem
    {
        public IPackageResolver Resolver = default!;
        public List<NuGetVersion> Versions = default!;

        public AggregatePackageResolverItem() { }
    }
}