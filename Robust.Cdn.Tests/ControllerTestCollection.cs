using Microsoft.AspNetCore.Mvc.Testing;
using System.Diagnostics.CodeAnalysis;

namespace Robust.Cdn.Tests;

/// <summary>
/// Defines a test collection that uses <c>DatabaseFixture</c> for cleanup.
/// </summary>
[CollectionDefinition("ControllerTests")]
[ExcludeFromCodeCoverage]
public sealed class ControllerTestCollection
    : ICollectionFixture<WebApplicationFactory<Program>>, ICollectionFixture<DatabaseFixture>
{
}
