using System.Reflection;

namespace Ondewo.Nlu.Client.Tests
{
    /// <summary>
    /// The single product-specific anchor of the reflection-driven suite in
    /// <see cref="GeneratedStubsTests"/>.
    /// <para>
    /// Every ONDEWO csharp client ships the same <c>GeneratedStubsTests.cs</c>; only this file and
    /// the concrete, product-specific suite beside it change from product to product. Pointing at
    /// a generated type rather than loading the assembly by name keeps it a compile-time error
    /// when the stubs are missing, instead of a test that fails at run time.
    /// </para>
    /// </summary>
    internal static class ProductStubs
    {
        /// <summary>The assembly holding the generated stubs under test.</summary>
        internal static Assembly Assembly => typeof(global::Ondewo.Nlu.User).Assembly;
    }
}
