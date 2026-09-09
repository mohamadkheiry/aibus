# AiBus model catalog

The built-in catalog snapshot was re-verified on **2026-08-08** against primary vendor documentation. Prices are stored in USD using the billing unit published by each vendor. Tiered, modality-specific, and effective-dated prices are preserved as separate pricing components; a missing public production price is represented as `contact_sales`, never estimated. This refresh also incorporates the current GPT-5.6 Terra and Luna standard/long-context rates while preserving administrator price overrides.

The snapshot key `catalog.model_snapshot` makes catalog updates idempotent. A new snapshot updates the built-in catalog once, while later administrator edits survive application restarts. Custom providers and custom models are not deactivated by catalog synchronization.

## Official sources

| Provider | Models and pricing |
|---|---|
| OpenAI | <https://developers.openai.com/api/docs/models/all>, <https://developers.openai.com/api/docs/pricing> |
| Google Gemini | <https://ai.google.dev/gemini-api/docs/models>, <https://ai.google.dev/gemini-api/docs/pricing> |
| Anthropic | <https://platform.claude.com/docs/en/about-claude/models/overview>, <https://platform.claude.com/docs/en/about-claude/pricing> |
| DeepSeek | <https://api-docs.deepseek.com/quick_start/pricing> |
| Moonshot Kimi | <https://platform.kimi.ai/docs/models> |
| Z.ai / GLM | <https://docs.z.ai/guides/overview/pricing> |
| xAI | <https://docs.x.ai/developers/models>, <https://docs.x.ai/developers/pricing> |
| Mistral AI | <https://docs.mistral.ai/models/overview>, <https://mistral.ai/pricing/api/> |
| Alibaba Qwen | <https://www.alibabacloud.com/help/en/model-studio/model-pricing> |
| Cohere | <https://docs.cohere.com/v1/docs/models>, <https://cohere.com/pricing> |
| ElevenLabs | <https://elevenlabs.io/docs/overview/models>, <https://elevenlabs.io/pricing/api> |
| Deepgram | <https://deepgram.com/pricing> |
| AssemblyAI | <https://www.assemblyai.com/pricing> |
| Google Cloud Speech | <https://cloud.google.com/speech-to-text/pricing>, <https://cloud.google.com/text-to-speech/pricing> |
| Amazon Web Services | <https://aws.amazon.com/transcribe/pricing/>, <https://aws.amazon.com/polly/pricing/> |
| Speechmatics | <https://www.speechmatics.com/pricing> |
| Gladia | <https://support.gladia.io/article/understanding-our-transcription-pricing-pv1atikh8y9c8sw7sudm3rcy> |

## Catalog rules

- Official stable, preview, regional, open-source-hosted, speech, realtime, image, video, embedding, moderation, OCR, rerank, translation, and agent offerings are represented when the vendor exposes them through an API.
- Deprecated or unverifiable vendor identifiers remain inactive so existing usage history is preserved without advertising an unusable model.
- Temporary promotions are described in pricing notes; the permanent list price is used as the default unless the vendor publishes a firm effective period.
- Native upstream base URLs and paths are stored separately from the public AiBus-compatible endpoint. This lets one brand use different hosts for chat, realtime, STT, TTS, agents, and media APIs.
