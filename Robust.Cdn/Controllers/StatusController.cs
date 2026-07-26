using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Robust.Cdn.DataAccessLayer;

namespace Robust.Cdn.Controllers;

[ApiController]
public class StatusController(Database db) : ControllerBase
{
    [HttpGet("control/status")]
    public IActionResult GetControlStatus(CancellationToken ct)
    {
        try
        {
            var versionCount = db.VersionCount();

            var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;

            return Ok(new
            {
                Status = "OK",
                Version = assemblyVersion?.ToString() ?? "Unknown",
                ContentVersions = versionCount
            });
        }
        catch
        {
            return StatusCode(500, new { Status = "Internal Server Error" });
        }
    }
}
