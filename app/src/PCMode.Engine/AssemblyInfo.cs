using System.Runtime.CompilerServices;

// The engine's tests reach two internals: the WASAPI seam that the Party
// hardware check drives directly, and the test signal it plays through it.
// Neither belongs on the public surface -- nothing in the shipped application
// constructs them, and the host builds its own.
[assembly: InternalsVisibleTo("PCMode.Engine.Tests")]
