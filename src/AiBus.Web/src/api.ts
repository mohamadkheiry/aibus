export const API = import.meta.env.VITE_API_URL || ''

export type User = { id:string; mobile:string; displayName:string; role:'SuperAdmin'|'User'; walletUsd:number; isSuspended:boolean; createdAtUtc:string; lastSeenAtUtc?:string }
export type Model = { id:string; providerId:string; modelId:string; displayName:string; modality:string; inputPricePerMillionUsd:number; outputPricePerMillionUsd:number; cachedInputPricePerMillionUsd?:number; contextWindow:number; supportsStreaming:boolean; supportsWebSocket:boolean; priceSyncedAtUtc:string; pricingSourceUrl:string; testPayloadJson?:string; provider:{id:string;name:string;slug:string;logoUrl:string} }
export type UserKey = { id:string;name:string;keyPrefix:string;isActive:boolean;requestLimit?:number;spendLimitUsd?:number;requestCount:number;spentUsd:number;accessMode:string;modelRules:string[];createdAtUtc:string;lastUsedAtUtc?:string }

export const token = () => localStorage.getItem('aibus_token')
export async function request<T=unknown>(path:string, options:RequestInit={}) : Promise<T> {
  const headers = new Headers(options.headers)
  if (!headers.has('Content-Type') && options.body) headers.set('Content-Type','application/json')
  if (token()) headers.set('Authorization',`Bearer ${token()}`)
  const res = await fetch(`${API}${path}`, {...options,headers})
  if (res.status === 204) return undefined as T
  const body = await res.json().catch(()=>({message:'خطای غیرمنتظره از سرور'}))
  if (!res.ok) throw new Error(body.message || body.error?.message || 'عملیات ناموفق بود')
  return body as T
}

export const money = (n:number, digits=2) => new Intl.NumberFormat('fa-IR',{minimumFractionDigits:digits,maximumFractionDigits:digits}).format(n)
export const number = (n:number) => new Intl.NumberFormat('fa-IR',{notation:n>999999?'compact':'standard',maximumFractionDigits:1}).format(n)
export const dateTime = (v:string|Date) => new Intl.DateTimeFormat('fa-IR-u-ca-persian',{dateStyle:'medium',timeStyle:'short'}).format(new Date(v))
