# Contributing

Thanks for helping with Grimoire. This repository contains public Windows source; no public binary release is available yet. The maintainer is `lodestoner`. Use your own Git author identity for contributions.

- **Bugs and ideas:** use the issue templates. Give the version and Windows build, steps to reproduce and the feature involved. Do not paste raw logs, selected text, file paths, credentials or provider responses; redact a minimal excerpt first.
- **Spells:** propose a generic, provider-neutral `spells.ini` section without personal endpoints or tokens.
- **Code:** C# / .NET 10, WinForms with WPF imaging. Use the SDK in `global.json`, locked restore and the existing single-file, self-contained build. Run the privacy audit and Python tests; Windows CI runs the synthetic app regression runner and installer lifecycle test.
- **Input testing:** use `Grimoire.exe --test-chord U`, `--test-click` and `--test-inject` on a disposable test profile. .NET `SendKeys` journal playback interferes with other injected input. Keep the everyday installation untouched.
- **Security:** preserve user data and explicit provider selection. Demonstrate privacy boundaries with behavioral tests, and record what still needs interactive Windows verification. See [SECURITY.md](SECURITY.md).

By contributing, you agree your contribution is released under the MIT license in [LICENSE](LICENSE).
