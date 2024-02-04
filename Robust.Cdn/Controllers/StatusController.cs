using System.Reflection;
using Dapper;
using Microsoft.AspNetCore.Mvc;

namespace Robust.Cdn.Controllers;

[ApiController]
public class StatusController : ControllerBase
{
    private readonly Database _db;

    public StatusController(Database db)
    {
        _db = db;
    }

    [HttpGet("control/status")]
    public async Task<IActionResult> GetControlStatus()
    {
        var con = _db.Connection;
        con.BeginTransaction(deferred: true);

        var versionCount = con.QuerySingleOrDefault<int>(
            "SELECT COUNT(Id) FROM ContentVersion");

        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;

        return Ok(new
        {
            Status = "OK",
            Version = assemblyVersion?.ToString() ?? "Unknown",
            ContentVersions = versionCount
        });
    }
}
