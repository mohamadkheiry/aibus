# AiBus

پلتفرم یکپارچه و فارسی فروش API مدل‌های هوش مصنوعی با backend دات‌نت و داشبورد React. کاربران با OTP سرویس SMS.ir وارد می‌شوند، کیف پول دلاری خود را با پرداخت ریالی زرین‌پال شارژ می‌کنند و از یک API سازگار با OpenAI برای دسترسی کنترل‌شده به چندین شرکت استفاده می‌کنند.

## قابلیت‌ها

- ورود بدون رمز با موبایل و OTP، با شماره `09015909044` به‌عنوان سوپرادمین اولیه
- Gateway سازگار با OpenAI برای `chat/completions`، حالت SSE Stream و WebSocket Realtime
- کاتالوگ بیش از ۳۲۰ سرویس فعال از ۱۷ ارائه‌دهنده شامل OpenAI، Gemini، Anthropic، DeepSeek، Kimi، GLM، xAI، Mistral، Qwen، Cohere، ElevenLabs، Deepgram، AssemblyAI، Google Cloud Speech، AWS، Speechmatics و Gladia
- چند API key برای هر Provider، failover، موجودی اولیه، باقی‌مانده و هشدار کمبود اعتبار
- قیمت چندواحدی متن و صوت شامل توکن متن/صوت/تصویر، دقیقه، ساعت، ثانیه، کاراکتر، پیام و صدا، همراه لینک رسمی منبع و تاریخ snapshot
- سرویس‌های تبدیل صوت‌به‌متن، متن‌به‌صوت، صوت‌به‌صوت، Live/Realtime، ترجمه هم‌زمان، دوبله، طراحی و شبیه‌سازی صدا
- کیف پول دلاری، تبدیل به ریال با نرخ و کارمزد قابل تنظیم، Request/Verify زرین‌پال و ledger تراکنش‌ها
- کلیدهای API کاربر با هش یک‌طرفه، سقف درخواست، سقف هزینه و سیاست `all` / `allow` / `deny` برای مدل‌ها
- کسر مصرف بر اساس usage واقعی پاسخ Provider و ثبت trace، latency، توکن و هزینه
- پنل حرفه‌ای RTL، حالت تاریک/روشن، نمودارهای مصرف و هزینه، کاتالوگ و آزمایشگاه مدل
- صفحه‌ی اصلی معرفی محصول با طراحی Responsive، نمایش مزیت‌ها و مسیر مستقیم ورود به پنل OTP
- پنل سوپرادمین برای کاربران، شارژ دستی، تعلیق، ورود امن به پنل کاربر، مدل‌ها، قیمت‌ها، کلیدها و تنظیمات
- Web analytics شامل بازدید یکتا، آنلاین‌ها، IP، مرورگر، سیستم‌عامل، Referrer و زمان شمسی
- رمزنگاری اسرار با ASP.NET Core Data Protection؛ SMS.ir API Key طبق نیاز از پنل سوپرادمین قابل مشاهده است

## اجرای توسعه

پیش‌نیاز: .NET SDK 8 و Node.js 22.

```powershell
dotnet run --project src/AiBus.Api --urls http://localhost:5050
```

در پنجره دوم:

```powershell
cd src/AiBus.Web
npm install
npm run dev
```

سپس `http://localhost:5173` را باز کنید. تا وقتی SMS.ir تنظیم نشده، در محیط Development کد OTP تست در پاسخ و فرم ورود نمایش داده می‌شود. این رفتار در Production غیرفعال است.

برای راه‌اندازی اولیه‌ی محلی Production می‌توان `EXPOSE_SUPERADMIN_OTP=true` را موقتاً در `.env` قرار داد؛ در این حالت کد فقط برای شماره‌ی سوپرادمین `09015909044` روی فرم نمایش داده می‌شود. بلافاصله پس از ثبت SMS.ir در تنظیمات، آن را `false` کنید و `docker compose up -d` را اجرا کنید.

## اجرای Docker

فایل `.env` بسازید و یک JWT key تصادفی حداقل ۶۴ کاراکتری قرار دهید:

```powershell
Copy-Item .env.example .env
docker compose up --build -d
```

داشبورد روی `http://localhost:8088` در دسترس است. دیتابیس و کلیدهای Data Protection در volumeهای جداگانه ماندگار می‌شوند.

## تنظیم اولیه سوپرادمین

۱. با موبایل `09015909044` وارد شوید.
۲. در «تنظیمات سامانه»، API Key و Template ID سرویس SMS.ir و Merchant ID زرین‌پال را ثبت کنید.
۳. نرخ معیار دلار و درصد کارمزد/مالیات را تنظیم کنید.
۴. در «ارائه‌دهندگان و کلیدها» برای هر شرکت یک یا چند کلید ثبت و اتصال را تست کنید.
۵. موجودی اولیه و آستانه هشدار هر کلید را وارد کنید.

## فراخوانی API

کلید ساخته‌شده توسط کاربر در پنل به‌صورت ماسک‌شده نمایش داده می‌شود و مالک آن می‌تواند با دکمه چشم، مقدار کامل رمزنگاری‌شده را دوباره مشاهده و کپی کند:

```bash
curl http://localhost:5050/v1/chat/completions \
  -H "Authorization: Bearer aibus_..." \
  -H "Content-Type: application/json" \
  -d '{"model":"gpt-5.6-luna","messages":[{"role":"user","content":"سلام"}],"stream":false}'
```

فهرست مدل‌های مجاز:

```bash
curl http://localhost:5050/v1/models -H "Authorization: Bearer aibus_..."
```

Realtime با `ws://localhost:5050/v1/realtime?model=gpt-realtime-2.1` و همان Bearer token قابل استفاده است.

## منابع رسمی قیمت seed

کاتالوگ و قیمت‌ها در snapshot تاریخ ۲۰۲۶-۰۷-۲۷ با اسناد رسمی هر ارائه‌دهنده تطبیق داده شده‌اند و لینک مرجع در تک‌تک رکوردهای مدل ذخیره شده است. فهرست کامل منابع و قواعد همگام‌سازی در [MODEL_CATALOG.md](MODEL_CATALOG.md) قرار دارد. منابع اصلی شامل:

- [OpenAI API pricing](https://developers.openai.com/api/docs/pricing)
- [Gemini API pricing](https://ai.google.dev/gemini-api/docs/pricing)
- [Claude API pricing](https://platform.claude.com/docs/en/about-claude/pricing)
- [DeepSeek models & pricing](https://api-docs.deepseek.com/quick_start/pricing)
- [Moonshot/Kimi models](https://platform.kimi.ai/docs/models)
- [Z.ai/GLM pricing](https://docs.z.ai/guides/overview/pricing)
- [Alibaba Model Studio pricing](https://www.alibabacloud.com/help/en/model-studio/model-pricing)
- [ElevenLabs API pricing](https://elevenlabs.io/pricing/api)
- [Deepgram pricing](https://deepgram.com/pricing)
- [AssemblyAI pricing](https://www.assemblyai.com/pricing)
- [Google Cloud Speech-to-Text pricing](https://cloud.google.com/speech-to-text/pricing)
- [Google Cloud Text-to-Speech pricing](https://cloud.google.com/text-to-speech/pricing)
- [Amazon Transcribe pricing](https://aws.amazon.com/transcribe/pricing/)
- [Amazon Polly pricing](https://aws.amazon.com/polly/pricing/)
- [Speechmatics pricing](https://www.speechmatics.com/pricing)
- [Gladia transcription pricing](https://support.gladia.io/article/understanding-our-transcription-pricing-pv1atikh8y9c8sw7sudm3rcy)

قیمت Providerها ممکن است بدون اطلاع تغییر کند؛ به همین دلیل پنل، تاریخ sync و لینک رسمی را نمایش می‌دهد و سوپرادمین می‌تواند هر قیمت را فوری ویرایش یا مدل را غیرفعال کند.

## امنیت و Production

- `Jwt__Key` پیش‌فرض فقط برای توسعه است و باید در Production جایگزین شود.
- کلیدهای کاربران برای اعتبارسنجی با SHA-256 تطبیق داده می‌شوند و یک نسخه رمزنگاری‌شده با Data Protection فقط برای نمایش مالک‌محور نگهداری می‌شود؛ پاسخ‌های حاوی کلید cache نمی‌شوند.
- کلیدهای قدیمی hash-only بدون تغییر معتبر می‌مانند؛ پس از اولین استفاده به‌صورت امن backfill می‌شوند یا مالک می‌تواند با هشدار صریح آن‌ها را تعویض کند.
- کلیدهای Provider، کاربران، SMS.ir و زرین‌پال با Data Protection رمزنگاری می‌شوند؛ volume کلیدها باید backup شود.
- پشت reverse proxy حتماً HTTPS، rate limiting لبه، backup دیتابیس و مانیتورینگ فعال کنید.
- موجودی wallet و ledger مالی باید در استقرار پرترافیک روی پایگاه داده تراکنشی مدیریت و با reconciliation دوره‌ای کنترل شود.

## تست

```powershell
dotnet test AiBus.slnx -c Release
cd src/AiBus.Web
npm audit --omit=dev
npm run build
```

## Multimedia gateway endpoints

- Text to speech: `POST /v1/audio/speech` with a JSON body and a Bearer AiBus API key.
- Speech to text: `POST /v1/audio/transcriptions` with `multipart/form-data`, an audio `file`, and a Bearer AiBus API key.
- Live audio and realtime services: `WS /v1/realtime?model=MODEL_ID`.

Browser WebSocket clients cannot set an `Authorization` header. They must send the
subprotocols `aibus-realtime` and `aibus-key.YOUR_AIBUS_API_KEY`; the gateway accepts
`aibus-realtime` and keeps the raw key out of the URL, access logs, and browser history.
The dashboard laboratory generates ready-to-run cURL, JavaScript, Python, C#, PHP,
and Go recipes for each supported transport.
