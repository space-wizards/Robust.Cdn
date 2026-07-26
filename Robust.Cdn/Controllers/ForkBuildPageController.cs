using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.DataAccessLayer;
using Robust.Cdn.DataAccessLayer.Models;

namespace Robust.Cdn.Controllers;

[Controller]
[Route("/fork/{fork}")]
public sealed class ForkBuildPageController(
    ManifestDatabase database,
    IOptions<ManifestOptions> manifestOptions)
    : Controller
{
    [HttpGet]
    public IActionResult Index(string fork)
    {
        if (!TryCheckBasicAuth(fork, out var errorResult))
            return errorResult;

        database.StartTransaction();

        var versions = database.ListForkVersions(fork, limit: 50);

        return View(new Model
        {
            Fork = fork,
            Options = manifestOptions.Value.Forks[fork],
            Versions = versions
        });
    }

    private bool TryCheckBasicAuth(
        string fork,
        [NotNullWhen(false)] out IActionResult? errorResult)
    {
        return ForkManifestController.TryCheckBasicAuth(HttpContext, manifestOptions.Value, fork, out errorResult);
    }

    public sealed class Model
    {
        public required string Fork;
        public required ManifestForkOptions Options;
        public required List<BuildVersionArtifactInfo> Versions;
    }
}
