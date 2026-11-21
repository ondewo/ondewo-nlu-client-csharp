using System.Runtime.CompilerServices;

// The test project drives the Keycloak provider's background loop, its shrunk timings and its registry
// factory seam directly; none of that is public API.
[assembly: InternalsVisibleTo("Ondewo.NLU.Client.Tests")]
