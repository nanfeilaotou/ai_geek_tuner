# AI Provider Foundation — Web Research Report (with sources)

Research for AIGeekTuner (.NET 8 WPF) "AI Provider Foundation": provider profiles + credentials + OpenAI-compatible transport, presets: Ollama Native (http://localhost:11434), LM Studio (http://127.0.0.1:1234/v1), Custom OpenAI-Compatible.
All conclusions below were checked against official documentation fetched during this session (local copies kept in `.research-tmp/`). Nothing GPL-licensed was copied; only design ideas are described.

---

## 1. OpenCode (sst → anomalyco, opencode.ai) provider configuration

Design conclusions:
- **Providers are a map keyed by a stable, arbitrary string ID** ("custom provider ID ... can be any string you want", e.g. `lmstudio`, `atomic-chat`, `ollama`). DisplayName is a separate `name` field ("display name for the provider in the UI"). → Split stable id vs displayName is a validated pattern.
- **Protocol/"kind" is selected by a package field**: `npm` — for any OpenAI-compatible API you set `"npm": "@ai-sdk/openai-compatible"`. Built-in catalogs come from models.dev (75+ providers).
- **`options` is a passthrough object to the SDK**: `options.baseURL` (e.g. `http://127.0.0.1:1234/v1`), `options.headers` (custom headers), `options.apiKey`, plus provider-specific extras (AWS `region`/`profile`/`endpoint` — `endpoint` documented as an alias for generic `baseURL`).
- **`models` is a map of model ID → config**: `{ "google/gemma-3n-e4b": { "name": "Gemma 3n-e4b (local)", "limit": { "context": 128000, "output": 65536 } } }`; each model entry supports `name` (display), `id` (remap, e.g. Azure/Bedrock ARN), `options`, `limit`. For local servers, **model IDs must match the `id` returned by GET /v1/models** ("run `curl http://127.0.0.1:1337/v1/models` to list the ids").
- **Model reference format is `provider_id/model_id`** — docs/models: "The format is the same as in the config file: provider_id/model_id" (config example `"model": "anthropic/claude-sonnet-4-20250514"`; "The format here is provider/model"). Nested slashes are allowed (provider `lmstudio` + model `google/gemma-3n-e4b`).
- **Credentials**: (a) `/connect` TUI command stores API keys in `~/.local/share/opencode/auth.json`; (b) config variable substitution — `{env:VARIABLE_NAME}` ("If the environment variable is not set, it will be replaced with an empty string") and `{file:path}` ("Keeping sensitive data like API keys in separate file"), e.g. `"options": { "apiKey": "{env:ANTHROPIC_API_KEY}" }` or `"{file:~/.secrets/openai-key}"`; (c) some providers read well-known env vars directly (e.g. `NVIDIA_API_KEY`).
- Also present: `blacklist`/`whitelist` arrays of model IDs to filter the picker.

Sources:
- https://opencode.ai/docs/providers/ (custom provider examples: lmstudio/ollama/atomic-chat/llama.cpp, npm/name/options/models, auth.json)
- https://opencode.ai/docs/config/ (variable substitution {env:VAR}, {file:path}; apiKey options)
- https://opencode.ai/docs/models/ (model reference format `provider_id/model_id`)
- https://raw.githubusercontent.com/anomalyco/opencode/master/packages/web/src/content/docs/providers.mdx (doc source)

---

## 2. Open WebUI — OpenAI-compatible connections

Design conclusions:
- **Protocol-oriented design**: "Open WebUI is built around Standard Protocols … expects your backend to follow the universal Chat Completions standard"; connections are just **URL + API Key** entries ("Manage OpenAI API Connections").
- **KEY TAKEAWAY — VERIFIED**: an official warning box states connection verification is done by "calling the provider's `/models` endpoint using a standard `Bearer` token", and when a provider doesn't implement /models or uses non-standard auth: "The connection verification will **fail with an error** (e.g., 400, 401 or 403). This does **not** mean the provider is incompatible: **chat completions will still work**." The remedy is to "**manually add model names** to the **Model IDs (Filter)** allowlist in the connection settings" — typed model IDs are added with a "+" icon (duplicates refused with "Model ID is already added"), and "The models will then appear in your model selector **even though the connection verification showed an error**."
- **Required endpoints table**: `/v1/models` GET = "**Recommended** — Used for model discovery … **If not available, add models to the allowlist manually**"; `/v1/chat/completions` POST = "**Yes** (required)". → /models failure must NOT equal provider invalid; chat-completions is the real liveness/validity probe.
- **Verify Connection behavior**: an explicit **Verify Connection** button in the connection dialog; success shows an alert ("Server connection verified" / "you should see a success alert"). Failure is non-fatal (see above).
- Extras worth copying: per-connection **enable/disable toggle** ("deactivate a provider while preserving its configuration"); model-list fetch **timeout** env `AIOHTTP_CLIENT_TIMEOUT_MODEL_LIST` (default 10 s) with troubleshooting for unreachable endpoints; a per-connection **Provider hint** dropdown (Default / Azure OpenAI / llama.cpp / LM Studio / LiteLLM) that unlocks server-specific features; **LM Studio preset row: URL `http://localhost:1234/v1`, API Key "Leave blank (or `lm-studio` as placeholder)"**; Open WebUI passes "standard OpenAI parameters such as `temperature`, `top_p`, `max_tokens` (or `max_completion_tokens`), `stop`, `seed`, and `logit_bias`".

Sources:
- https://docs.openwebui.com/getting-started/quick-start/connect-a-provider/starting-with-openai-compatible/ (fetched as raw mdx: https://raw.githubusercontent.com/open-webui/docs/main/docs/getting-started/quick-start/connect-a-provider/starting-with-openai-compatible.mdx)
- https://docs.openwebui.com/getting-started/quick-start/connect-a-provider/starting-with-openai-compatible/#local-servers (LM Studio preset)

---

## 3. LM Studio official docs

Design conclusions:
- **Endpoints**: `GET /v1/models` — "Returns the models visible to the server. The list may include all downloaded models when Just-In-Time loading is enabled." `POST /v1/chat/completions` — OpenAI-compatible chat; official client examples use `base_url="http://localhost:1234/v1"`.
- **API key**: "**By default, LM Studio does not require authentication** for API requests. To enable authentication … toggle the switch in the Developers Page > Server Settings"; when enabled, requests must carry `Authorization: Bearer $LM_API_TOKEN` with a valid API token. Official examples pass a placeholder key `api_key="lm-studio"` (an SDK requirement), and Open WebUI's docs say leave blank or `lm-studio`. → With auth off, **no key or any placeholder key works**; with auth on, a real token is required.
- **Structured output**: supported via `response_format` `{ "type": "json_schema", "json_schema": { "name": ..., "strict": ..., "schema": {...} } }` on /v1/chat/completions — "It follows the same format as OpenAI's … Structured Output API and is expected to work via the OpenAI client SDKs". Result arrives as a **string** in `choices[0].message.content` and must be parsed. Caveat: "Not all models are capable of structured output, particularly LLMs below 7B parameters." Engines: GGUF → llama.cpp grammar-based sampling; MLX → Outlines.
- **max_tokens vs max_completion_tokens**: the documented supported payload parameter list is `model, top_p, top_k, messages, temperature, max_tokens, stream, stop, presence_penalty, frequency_penalty, logit_bias, repeat_penalty, seed` — **`max_tokens` is listed; `max_completion_tokens` is not documented**. Design should send `max_tokens` to LM Studio. **UNVERIFIED**: whether `max_completion_tokens` is silently accepted or ignored.

Sources:
- https://raw.githubusercontent.com/lmstudio-ai/docs/main/1_developer/3_openai-compat/models.md (GET /v1/models)
- https://raw.githubusercontent.com/lmstudio-ai/docs/main/1_developer/3_openai-compat/chat-completions.md (supported params incl. max_tokens; lm-studio placeholder key)
- https://beta.lmstudio.ai/docs/developer/core/authentication (fetched; default no auth, optional bearer tokens)
- https://beta.lmstudio.ai/docs/developer/openai-compat/structured-output (+ raw source https://raw.githubusercontent.com/lmstudio-ai/docs/main/1_developer/3_openai-compat/structured-output.md)

---

## 4. Ollama official docs

Design conclusions:
- **Native API**: `POST /api/chat` — params `model` (required, `model:tag` naming), `messages` (role/content, roles system|user|assistant|tool), `tools`, `think`, plus advanced `format`, `options` (temperature etc.), `stream` (default streaming; `"stream": false` for single object), `keep_alive` (default 5m). `GET /api/tags` — "List models that are available locally"; response `{ "models": [ { "name": "deepseek-r1:latest", "model": ..., "modified_at", "size", "digest", "details": { "family", "parameter_size", "quantization_level", ... } } ] }`.
- **Native structured output**: "Structured outputs are supported by providing a **JSON schema in the `format` parameter**" — `format` can be `"json"` (JSON mode) or a JSON schema object; docs also recommend instructing the model to use JSON in the prompt.
- **OpenAI compatibility layer** (docs.ollama.com "OpenAI compatibility"): endpoints `/v1/chat/completions`, `/v1/completions`, `/v1/models`, `/v1/models/{model}` (+ /v1/responses style fields on that page). Supported features on /v1/chat/completions: chat completions, streaming, JSON mode, reproducible outputs, vision, tools, reasoning/thinking control, logprobs. **Supported request fields**: `model, messages (text + image content parts), frequency_penalty, presence_penalty, response_format, seed, stop, stream, stream_options (include_usage), temperature, top_p, max_tokens, tools, reasoning_effort, tool_choice, logit_bias, user, n`. → **`max_tokens` documented; `max_completion_tokens` NOT documented** (UNVERIFIED whether it's accepted).
- **response_format / structured outputs over compat layer**: "Structured outputs work through the OpenAI-compatible API via **response_format**" (capabilities/structured-outputs). GitHub issue #10001 tracks further json_schema compliance work — schema support is good but evolving.
- **Auth**: "**No authentication is required when accessing Ollama's API locally** via http://localhost:11434." API keys (`OLLAMA_API_KEY`, `Authorization: Bearer`) are only for the **ollama.com cloud API** (https://ollama.com/api), cloud models, publishing, private models. The client examples mark `api_key='ollama'` as "**required but ignored**" (that's the OpenAI SDK's requirement, not Ollama's). → AIGeekTuner's Ollama preset should treat credential as optional.
- **/v1/models notes**: `created` = when the model was last modified; `owned_by` = ollama username, defaulting to "library". Historical doc (experimental era) notes: `seed` forces temperature 0; `finish_reason` always `stop`; `usage.prompt_tokens` is 0 when prompt eval is cached.

Sources:
- https://docs.ollama.com/api/openai-compatibility (current compat matrix)
- https://docs.ollama.com/api/authentication (local no-auth; cloud keys)
- https://docs.ollama.com/capabilities/structured-outputs (format param + response_format)
- https://github.com/ollama/ollama/blob/main/docs/api.md (native /api/chat, /api/tags; note "API docs are moving to https://docs.ollama.com/api")
- https://github.com/ollama/ollama/blob/4f1c2a5cdf1d72d8bd034120643aceee6e7d4894/docs/openai.md (experimental-era field list & notes)
- https://github.com/ollama/ollama/issues/10001 (json_schema response_format compliance tracking)

---

## 5. OpenAI Chat Completions API

Design conclusions:
- **Minimal request shape**: `POST /v1/chat/completions` with **required: `model` + `messages`** (messages minItems 1) — per the official OpenAPI schema `CreateChatCompletionRequest`. Everything else is optional (`temperature`, `max_completion_tokens`, `response_format`, `stream`, `stop`, `seed`, `top_p`, ...).
- **max_tokens vs max_completion_tokens** (spec text): `max_tokens` — "This value is now **deprecated in favor of `max_completion_tokens`**, and is **not compatible with o-series models**". `max_completion_tokens` — "An upper bound for the number of tokens that can be generated for a completion, **including visible output tokens and reasoning tokens**." → For OpenAI send `max_completion_tokens`; for LM Studio/Ollama send `max_tokens` (per their docs above). A per-provider "token-limit field name" capability flag is warranted.
- **response_format types** (spec): oneOf `text` | `{ "type": "json_object" }` — "enables the **older JSON mode**, which ensures the message the model generates is valid JSON" | `{ "type": "json_schema", "json_schema": {...} }` — "enables **Structured Outputs** which ensures the model will match your supplied JSON schema … Using `json_schema` is preferred for models that support it".
- **Error envelope** (spec `Error`/`ErrorResponse`): body is `{ "error": { "code": string|null, "message": string, "param": string|null, "type": string } }` — all four fields required in the schema. **Invalid API key → HTTP 401** with `type: "invalid_request_error"` and `code: "invalid_api_key"`, message "Incorrect API key provided: …" (Help Center article "Incorrect API key provided"; community threads confirm 401). The spec defines the envelope shape; the specific `invalid_api_key` code string is documented via Help Center/community — treat as **VERIFIED (shape) / widely-reported (code string)**.

Sources:
- https://github.com/openai/openai-openapi/blob/master/openapi.yaml (fetched; CreateChatCompletionRequest, max_tokens deprecation, response_format, Error/ErrorResponse)
- https://platform.openai.com/docs/api-reference/chat/create (canonical reference page)
- https://platform.openai.com/docs/guides/structured-outputs (structured outputs guide)
- https://help.openai.com/en/articles/6882433-incorrect-api-key-provided (401 invalid key; page blocked automated fetch, existence verified via search)
- https://community.openai.com/t/i-keep-getting-error-401-when-trying-to-load-my-api-key-in-python-powershell/1147898 (401 confirmation)

---

## 6. .NET System.Security.Cryptography.ProtectedData (DPAPI)

Design conclusions:
- **NuGet package**: `System.Security.Cryptography.ProtectedData` — required for .NET 8 (the type is in assembly `System.Security.Cryptography.ProtectedData.dll`; "Package: System.Security.Cryptography.ProtectedData"). Package v8.0.0 supports **net8.0** (also net9.0/net10.0, .NETFramework 4.6.2, netstandard2.0 with a System.Memory dependency; **no dependencies on net8.0**). For a net8.0-windows WPF app: add the package (it is Windows-only by nature).
- **Windows-only DPAPI**: the class "provides access to the Data Protection API (DPAPI) available in Windows operating systems … protection using the user or machine credentials"; "Because it depends on DPAPI, the **ProtectedData class is supported on the Windows platform only**. Its use on .NET Core on platforms other than Windows throws a `PlatformNotSupportedException`." Wraps `CryptProtectData`/`CryptUnprotectData`. Intended for "passwords, keys, and connection strings" — exactly our credential-at-rest case.
- **DataProtectionScope.CurrentUser (= 0)**: "The protected data is associated with the current user. **Only threads running under the current user context can unprotect the data.**" `LocalMachine` (= 1): "Any process running on the computer can unprotect data … **Use this value only when you trust every account on a computer. For most situations, you should use the CurrentUser**" scope. → CurrentUser is the right default for per-user stored API keys.
- **Entropy parameter best practice**: `Protect(byte[] userData, byte[]? optionalEntropy, DataProtectionScope scope)` — entropy is **optional** (nullable). MSDN how-to: "Create random entropy. Call the static Protect method while passing an array of bytes to encrypt, **the entropy**, and the data protection scope" (same entropy must be supplied to `Unprotect`). Official examples pass a stable app-level `s_additionalEntropy` with CurrentUser scope ("The result can be decrypted only by the same current user"). Practice for AIGeekTuner: generate a random app-specific entropy blob once, persist it alongside config (it is not a secret — it adds application binding on top of user binding), and always pass the identical bytes to Protect/Unprotect.

Sources:
- https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata?view=net-8.0 (fetched)
- https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope?view=net-8.0 (fetched; CurrentUser/LocalMachine semantics + caution)
- https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata.protect?view=net-8.0 (fetched; optionalEntropy)
- https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection?tabs=net (fetched; how-to incl. entropy)
- https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/ (fetched; TFM matrix, Windows-only note)
- https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/8.0.0 (v8.0.0 for net8)

---

## Cross-cutting design takeaways for AIGeekTuner (all sourced above)

1. Stable provider **ID** = arbitrary string, decoupled from **displayName** (OpenCode §1); preset IDs: `ollama-native`, `lmstudio`, `openai-compatible`.
2. Model IDs for OpenAI-compatible servers must match `GET /models` ids, but **must be user-enterable manually when /models fails** (OpenCode §1 + Open WebUI §2). Draft-models-fetch failure should mark models "undiscovered", never the provider "invalid".
3. **Test connection should probe the required endpoint** (POST /chat/completions with a minimal 1-message request) — Open WebUI's "Verify Connection" only checks GET /models and explicitly tolerates its failure (§2, §3).
4. Credential handling: optional for local presets (LM Studio default no-auth, any placeholder ok; Ollama local no-auth, api_key "required but ignored"); required for cloud-compatible endpoints; store with DPAPI CurrentUser + app entropy (§3, §4, §6).
5. Per-provider request dialect flags: token-limit field (`max_tokens` vs `max_completion_tokens`), structured-output transport (native `format` schema for Ollama native vs `response_format.json_schema` for OpenAI/LM Studio/Ollama-compat) (§3, §4, §5).
6. Save/apply lifecycle: connection enable/disable toggle that preserves config, and draft→save with non-blocking verification warnings matches Open WebUI's UX (§2).

UNVERIFIED items are marked inline (Ollama/LM Studio `max_completion_tokens` acceptance; OpenAI `invalid_api_key` code string fetched only via search metadata).
