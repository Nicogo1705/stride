# Shaders in Stride: SDSL, mixins, compositions, permutations

Two documents, meant to be read in order:

- [sdsl-guide.md](sdsl-guide.md) — the concepts. What a shader class is, how classes are mixed into one program, what `stage`, `stream`, `compose` and generics do, how an effect (`.sdfx`) turns parameters into permutations, how parameter keys reach a shader, and what the compiler pipeline does to your code on the way to the GPU. Ends with the pitfalls that cost the most hours.
- [sdsl-tutorials.md](sdsl-tutorials.md) — eight tutorials that build on each other, from a full-screen tint to wrapping a compiled material with a layer of your own. Each is a working pair of SDSL and C#.

The language reference lives in the manual: <https://doc.stride3d.net/latest/en/manual/graphics/effects-and-shaders/shading-language/index.html>. The compiler's own internals are described in the SDSL repository wiki: <https://github.com/stride3d/SDSL/wiki>. These two documents sit between the two: they explain how the pieces fit, with the engine's own shaders as the examples.
