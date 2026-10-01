# Third-Party Notices

This package (`com.gossipnet.ai-npc`) itself contains no third-party source code or
assets. It does declare the following runtime dependency, resolved by Unity's
Package Manager and not bundled in this repository:

## Newtonsoft.Json (via `com.unity.nuget.newtonsoft-json`)

- Used by: `Runtime/LLMBridge.cs`, `Runtime/ConversationStorage.cs` (JSON parsing/serialization)
- License: MIT
- Distributed by Unity Technologies as a UPM wrapper around
  [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json)
- Newtonsoft.Json license text: https://github.com/JamesNK/Newtonsoft.Json/blob/master/LICENSE.md

This package also calls out to third-party LLM APIs (OpenAI-compatible endpoints,
or the Anthropic API) at runtime, under credentials and terms of service that are
the responsibility of the package's user — no API client SDKs or credentials are
bundled here.
