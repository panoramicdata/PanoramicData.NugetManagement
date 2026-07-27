namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// One assessed package: the repository it lives in, its assessment, and which package it is.
/// A repository can host several packages, each assessed separately, so the package identity is
/// what distinguishes multiple occurrences of the same repository under one rule.
/// </summary>
/// <param name="RepositoryFullName">The repository full name, e.g. "panoramicdata/Highlight.Api".</param>
/// <param name="Assessment">The assessment result for this package.</param>
/// <param name="PackageId">The package identifier, when known.</param>
public sealed record AssessedPackage(string RepositoryFullName, RepoAssessment Assessment, string? PackageId = null);
