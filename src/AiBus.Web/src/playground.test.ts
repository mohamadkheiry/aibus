import { describe, expect, it } from 'vitest'
import { createCodeRecipe, type CodeRecipeLanguage } from './playgroundRecipes'

const endpoint = 'https://aibus.00f.ir/v1/chat/completions'
const payload = JSON.stringify({
  model: 'gpt-5-mini',
  messages: [{ role: 'user', content: 'سلام AiBus' }],
  stream: false
}, null, 2)

describe('playground code recipes', () => {
  it.each<CodeRecipeLanguage>(['curl', 'javascript', 'python', 'csharp', 'php', 'go'])(
    'builds a safe %s request from the editable JSON',
    language => {
      const recipe = createCodeRecipe(language, endpoint, payload)

      expect(recipe).toContain(endpoint)
      expect(recipe).toContain('YOUR_AIBUS_API_KEY')
      expect(recipe).toContain('gpt-5-mini')
      expect(recipe).toContain('Authorization')
    }
  )

  it('generates stream-aware JavaScript and Python examples', () => {
    expect(createCodeRecipe('javascript', endpoint, payload)).toContain('text/event-stream')
    expect(createCodeRecipe('python', endpoint, payload)).toContain("payload.get('stream')")
  })

  it('generates playable text-to-speech examples', () => {
    const speech = JSON.stringify({model:'tts-1',input:'سلام',voice:'alloy'})
    expect(createCodeRecipe('curl','https://aibus.00f.ir/v1/audio/speech',speech,'tts')).toContain('--output speech.mp3')
    expect(createCodeRecipe('javascript','https://aibus.00f.ir/v1/audio/speech',speech,'tts')).toContain('new Audio(url).play()')
    expect(createCodeRecipe('python','https://aibus.00f.ir/v1/audio/speech',speech,'tts')).toContain("open('speech.mp3', 'wb')")
  })

  it('generates multipart speech-to-text examples', () => {
    const transcription = JSON.stringify({model:'whisper-1',language:'fa'})
    expect(createCodeRecipe('curl','https://aibus.00f.ir/v1/audio/transcriptions',transcription,'stt')).toContain("file=@audio.wav")
    expect(createCodeRecipe('csharp','https://aibus.00f.ir/v1/audio/transcriptions',transcription,'stt')).toContain('MultipartFormDataContent')
    expect(createCodeRecipe('go','https://aibus.00f.ir/v1/audio/transcriptions',transcription,'stt')).toContain('multipart.NewWriter')
  })

  it('uses secure WebSocket subprotocols without putting the key in the URL', () => {
    const realtime = createCodeRecipe('javascript','https://aibus.00f.ir/v1/realtime',JSON.stringify({model:'gpt-realtime-2.1'}),'realtime')
    expect(realtime).toContain("'aibus-realtime'")
    expect(realtime).toContain('aibus-key.YOUR_AIBUS_API_KEY')
    expect(realtime).not.toContain('api_key=')
  })

  it('uses the GA realtime transcription event contract', () => {
    const recipe = createCodeRecipe('javascript','https://aibus.00f.ir/v1/realtime',JSON.stringify({model:'gpt-realtime-whisper',language:'fa',delay:'low'}),'realtime','speech_to_text')
    expect(recipe).toContain('transcription')
    expect(recipe).toContain('audio/pcm')
    expect(recipe).toContain('gpt-realtime-whisper')
    expect(recipe).toContain("type: 'input_audio_buffer.append'")
    expect(recipe).not.toContain('transcription_session.update')
  })

  it('uses the dedicated realtime translation event contract', () => {
    const recipe = createCodeRecipe('javascript','https://aibus.00f.ir/v1/realtime',JSON.stringify({model:'gpt-realtime-translate',target_language:'fa'}),'realtime','realtime_translation')
    expect(recipe).not.toContain('target_language')
    expect(recipe).toContain('language')
    expect(recipe).toContain("type: 'session.input_audio_buffer.append'")
    expect(recipe).not.toContain('"type":"translation"')
  })
})
