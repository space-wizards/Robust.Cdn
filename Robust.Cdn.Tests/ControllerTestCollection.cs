using Microsoft.AspNetCore.Mvc.Testing;

namespace Robust.Cdn.Tests;

/// <summary>
/// Defines a test collection that uses <c>DatabaseFixture</c> for cleanup.
/// </summary>
[CollectionDefinition("ControllerTests")]
public sealed class ControllerTestCollection
    : ICollectionFixture<WebApplicationFactory<Program>>, ICollectionFixture<DatabaseFixture>
{
}
