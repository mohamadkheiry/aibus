export const API = import.meta.env.VITE_API_URL || ''

export type User = { id:string; mobile:string; displayName:string; role:'SuperAdmin'|'User'; walletUsd:number; isSuspended:boolean; createdAtUtc:string; lastSeenAtUtc?:string }
export type PricingComponent = { label:string; unit:string; priceUsd:number|null; note?:string|null }
export type Model = { id:string; providerId:string; modelId:string; displayName:string; modality:string; inputModalities?:string[]; outputModality?:string; serviceType:string; endpointPath:string; upstreamBaseUrl?:string; upstreamPath?:string; region:string; isPreview:boolean; pricingComponents:PricingComponent[]; pricingNotes?:string; inputPricePerMillionUsd:number; outputPricePerMillionUsd:number; cachedInputPricePerMillionUsd?:number; contextWindow:number; supportsStreaming:boolean; supportsWebSocket:boolean; priceSyncedAtUtc:string; pricingSourceUrl:string; testPayloadJson?:string; provider:{id:string;name:string;slug:string;logoUrl:string} }
export type UserKey = { id:string;name:string;keyPrefix:string;canReveal:boolean;isActive:boolean;requestLimit?:number|null;spendLimitUsd?:number|null;requestCount:number;spentUsd:number;accessMode:string;modelRules:string[];createdAtUtc:string;lastUsedAtUtc?:string }

export type SafeApiError = { message:string;code:string;status?:number }

const providerErrorMessages={
  quota:'اعتبار سرویس هوش مصنوعی در حال حاضر کافی نیست. هزینه‌ای از کیف پول شما کسر نشد؛ لطفاً کمی بعد دوباره تلاش کنید یا با پشتیبانی تماس بگیرید.',
  rate:'سرویس هوش مصنوعی موقتاً با درخواست‌های زیادی روبه‌رو است. هزینه‌ای از کیف پول شما کسر نشد؛ لطفاً چند لحظه دیگر دوباره تلاش کنید.',
  auth:'اتصال این سرویس از سمت سامانه نیاز به بررسی دارد. هزینه‌ای از کیف پول شما کسر نشد؛ لطفاً با پشتیبانی تماس بگیرید.',
  unavailable:'سرویس هوش مصنوعی موقتاً در دسترس نیست. هزینه‌ای از کیف پول شما کسر نشد؛ لطفاً کمی بعد دوباره تلاش کنید.',
  rejected:'درخواست توسط سرویس مقصد پذیرفته نشد. ورودی درخواست را بررسی کنید؛ هزینه‌ای از کیف پول شما کسر نشد.',
  generic:'انجام درخواست ممکن نشد. لطفاً دوباره تلاش کنید یا با پشتیبانی تماس بگیرید.'
} as const

const recordOf=(value:unknown):Record<string,unknown>|null=>value!==null&&typeof value==='object'&&!Array.isArray(value)?value as Record<string,unknown>:null
const stringOf=(value:unknown)=>typeof value==='string'?value.trim():''
const searchableText=(value:unknown)=>{try{return typeof value==='string'?value:JSON.stringify(value)}catch{return ''}}

/**
 * Converts provider and gateway errors to a stable, user-safe envelope. Raw upstream
 * messages intentionally stay out of the result; Persian messages emitted by AiBus
 * itself are preserved so the backend remains the source of truth for wording.
 */
export function safeApiError(value:unknown,status?:number):SafeApiError{
  let parsed=value
  if(value instanceof Error)parsed={message:value.message}
  else if(typeof value==='string'){try{parsed=JSON.parse(value)}catch{parsed=value}}

  const body=recordOf(parsed),nested=recordOf(body?.error)
  const message=stringOf(nested?.message)||stringOf(body?.message)||stringOf(body?.detail)||stringOf(parsed)
  const upstreamCode=(stringOf(nested?.code)||stringOf(body?.code)||stringOf(nested?.type)||stringOf(body?.type)).toLowerCase()
  const haystack=`${upstreamCode} ${message} ${searchableText(parsed)}`.toLowerCase()
  const hasPersian=/[\u0600-\u06ff]/.test(message)

  const definiteQuota=upstreamCode==='provider_quota_exhausted'||/(insufficient[_ -]?quota|billing[_ -]?hard[_ -]?limit|billing details|out of credits|not enough credits|credit balance is too low|insufficient balance|monthly spend)/i.test(haystack)
  const rate=upstreamCode==='provider_rate_limited'||/(rate[_ -]?limit|too many requests|requests per (minute|second)|tokens per minute|quota[_ -]?exceeded|resource[_ -]?exhausted)/i.test(haystack)
  const quota=definiteQuota||(!rate&&/exceeded your current quota/i.test(haystack))
  const auth=upstreamCode==='provider_authentication_failed'||/(invalid[_ -]?api[_ -]?key|incorrect api key|authentication failed|unauthori[sz]ed|invalid authentication)/i.test(haystack)
  const unavailable=upstreamCode==='provider_unavailable'||/(service unavailable|upstream unavailable|bad gateway|gateway timeout|temporarily unavailable)/i.test(haystack)
  const rejected=upstreamCode==='upstream_request_rejected'||upstreamCode==='provider_request_rejected'

  const knownCode=quota?'provider_quota_exhausted':rate?'provider_rate_limited':auth?'provider_authentication_failed':unavailable?'provider_unavailable':rejected?'upstream_request_rejected':''
  if(hasPersian)return {message,code:knownCode||upstreamCode||'request_failed',...(status?{status}:{})}
  if(quota||status===402)return {message:providerErrorMessages.quota,code:'provider_quota_exhausted',...(status?{status}:{})}
  if(rate||status===429)return {message:providerErrorMessages.rate,code:'provider_rate_limited',...(status?{status}:{})}
  if(auth||status===401||status===403)return {message:providerErrorMessages.auth,code:'provider_authentication_failed',...(status?{status}:{})}
  if(unavailable||(status!==undefined&&status>=500))return {message:providerErrorMessages.unavailable,code:'provider_unavailable',...(status?{status}:{})}
  if(rejected||(status!==undefined&&status>=400))return {message:providerErrorMessages.rejected,code:'upstream_request_rejected',...(status?{status}:{})}
  return {message:providerErrorMessages.generic,code:'request_failed',...(status?{status}:{})}
}

export const safeApiErrorFromText=(text:string,status?:number)=>safeApiError(text,status)
export async function readApiError(response:Response):Promise<SafeApiError>{
  return safeApiErrorFromText(await response.text(),response.status)
}

export const token = () => localStorage.getItem('aibus_token')
export async function request<T=unknown>(path:string, options:RequestInit={}) : Promise<T> {
  const headers = new Headers(options.headers)
  if (!headers.has('Content-Type') && options.body) headers.set('Content-Type','application/json')
  if (token()) headers.set('Authorization',`Bearer ${token()}`)
  const res = await fetch(`${API}${path}`, {...options,headers})
  if (res.status === 204) return undefined as T
  const text = await res.text()
  let body:unknown
  try { body=text?JSON.parse(text):undefined } catch { body=text }
  if (!res.ok) throw new Error(safeApiError(body,res.status).message)
  return body as T
}

const formatWithDotDecimal = (n:number, options:Intl.NumberFormatOptions) =>
  new Intl.NumberFormat('fa-IR',options).formatToParts(n).map(part=>part.type==='decimal'?'.':part.value).join('')
export const money = (n:number, digits=2) => formatWithDotDecimal(n,{minimumFractionDigits:digits,maximumFractionDigits:digits})
export const usd = (n:number, digits=2) => new Intl.NumberFormat('en-US-u-nu-latn',{minimumFractionDigits:digits,maximumFractionDigits:digits}).format(n)
export const number = (n:number) => formatWithDotDecimal(n,{notation:n>999999?'compact':'standard',maximumFractionDigits:1})
export const chartNumber = (n:number) => {
  const absolute = Math.abs(n)
  if (absolute >= 1_000_000_000) return `${formatWithDotDecimal(n/1_000_000_000,{maximumFractionDigits:1})}B`
  if (absolute >= 1_000_000) return `${formatWithDotDecimal(n/1_000_000,{maximumFractionDigits:1})}M`
  if (absolute >= 1_000) return `${formatWithDotDecimal(n/1_000,{maximumFractionDigits:1})}K`
  return formatWithDotDecimal(n,{maximumFractionDigits:1})
}
export const dateTime = (v:string|Date) => new Intl.DateTimeFormat('fa-IR-u-ca-persian',{dateStyle:'medium',timeStyle:'short'}).format(new Date(v))
