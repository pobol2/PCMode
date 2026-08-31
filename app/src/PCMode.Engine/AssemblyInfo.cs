using System.Runtime.CompilerServices;

// The engine's tests reach two internals: the WASAPI seam that the audio
// hardware check drives directly, and the test signal it plays through the
// PCMode virtual endpoint. Neither belongs on the public surface -- nothing in
// the shipped application constructs them.
[assembly: InternalsVisibleTo("PCMode.Engine.Tests")]
