import { transcribeVoice } from '../net/conversationsApi.js';
import { t } from '../i18n/i18n.js';
import { showToast, userMessageForError } from '../ui/errors.js';

const MAX_RECORDING_MS = 45000;
const TARGET_SAMPLE_RATE = 16000;

const state = {
  button: null,
  input: null,
  stream: null,
  context: null,
  source: null,
  processor: null,
  chunks: [],
  sampleRate: 0,
  recording: false,
  transcribing: false,
  timer: null,
};

export function wireVoiceDictation() {
  state.button = document.getElementById('mic-btn');
  state.input = document.getElementById('composer-input');
  if (!state.button || !state.input) return;

  setButtonState('idle');
  state.button.addEventListener('click', (event) => {
    event.preventDefault();
    if (state.transcribing) return;
    if (state.recording) {
      stopRecordingAndTranscribe();
      return;
    }
    startRecording();
  });
}

async function startRecording() {
  if (!navigator?.mediaDevices?.getUserMedia) {
    showToast(t('voice.unsupported'), { tone: 'info' });
    return;
  }

  try {
    resetRecording();
    state.stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
    state.context = new AudioContextCtor();
    state.sampleRate = state.context.sampleRate || TARGET_SAMPLE_RATE;
    state.source = state.context.createMediaStreamSource(state.stream);
    state.processor = state.context.createScriptProcessor(4096, 1, 1);
    state.processor.onaudioprocess = handleAudioProcess;
    state.source.connect(state.processor);
    state.processor.connect(state.context.destination);
    state.recording = true;
    state.timer = window.setTimeout(() => stopRecordingAndTranscribe(), MAX_RECORDING_MS);
    setButtonState('recording');
    showToast(t('voice.listening'), { tone: 'info', durationMs: 1600 });
  } catch (err) {
    resetRecording();
    setButtonState('idle');
    showToast(userMessageForError(err, t('voice.microphoneUnavailable')), { tone: 'info' });
  }
}

function handleAudioProcess(event) {
  if (!state.recording) return;
  const input = event.inputBuffer.getChannelData(0);
  state.chunks.push(new Float32Array(input));
  const output = event.outputBuffer.getChannelData(0);
  output.fill(0);
}

async function stopRecordingAndTranscribe() {
  if (!state.recording) return;
  const chunks = state.chunks.slice();
  const sampleRate = state.sampleRate || TARGET_SAMPLE_RATE;

  stopAudioGraph();
  state.recording = false;
  state.transcribing = true;
  setButtonState('transcribing');

  try {
    const audio = flattenAudio(chunks);
    if (!audio.length) {
      showToast(t('voice.empty'), { tone: 'info' });
      return;
    }
    const wavBlob = encodeWav(downsampleAudio(audio, sampleRate, TARGET_SAMPLE_RATE), TARGET_SAMPLE_RATE);
    const payload = await transcribeVoice(wavBlob);
    const text = String(payload?.text || '').trim();
    if (!text) {
      showToast(t('voice.empty'), { tone: 'info' });
      return;
    }
    insertDictationText(text);
    showToast(t('voice.inserted'), { tone: 'info', durationMs: 1800 });
  } catch (err) {
    showToast(userMessageForError(err, t('voice.transcriptionImpossible')), { tone: 'info' });
  } finally {
    resetRecording();
    state.transcribing = false;
    setButtonState('idle');
  }
}

function stopAudioGraph() {
  if (state.timer) {
    window.clearTimeout(state.timer);
    state.timer = null;
  }
  try { state.processor?.disconnect(); } catch (_) {}
  try { state.source?.disconnect(); } catch (_) {}
  try { state.stream?.getTracks?.().forEach((track) => track.stop()); } catch (_) {}
  try { state.context?.close?.(); } catch (_) {}
}

function resetRecording() {
  stopAudioGraph();
  state.stream = null;
  state.context = null;
  state.source = null;
  state.processor = null;
  state.chunks = [];
  state.sampleRate = 0;
  state.recording = false;
}

function setButtonState(mode) {
  if (!state.button) return;
  state.button.classList.toggle('is-listening', mode === 'recording');
  state.button.classList.toggle('is-transcribing', mode === 'transcribing');
  state.button.setAttribute('aria-pressed', mode === 'recording' ? 'true' : 'false');
  const titleKey = mode === 'recording'
    ? 'voice.stopTitle'
    : mode === 'transcribing'
      ? 'voice.transcribingTitle'
      : 'voice.micTitle';
  state.button.dataset.i18nTitle = titleKey;
  state.button.dataset.i18nAriaLabel = titleKey;
  state.button.title = t(titleKey);
  state.button.setAttribute('aria-label', t(titleKey));
}

function insertDictationText(text) {
  const input = state.input;
  if (!input) return;
  const value = input.value || '';
  const start = Number.isFinite(input.selectionStart) ? input.selectionStart : value.length;
  const end = Number.isFinite(input.selectionEnd) ? input.selectionEnd : start;
  const needsSpaceBefore = start > 0 && !/\s$/.test(value.slice(0, start));
  const needsSpaceAfter = end < value.length && !/^\s/.test(value.slice(end));
  const nextText = `${needsSpaceBefore ? ' ' : ''}${text}${needsSpaceAfter ? ' ' : ''}`;
  input.setRangeText(nextText, start, end, 'end');
  input.dispatchEvent(new Event('input', { bubbles: true }));
  input.focus();
}

function flattenAudio(chunks) {
  const total = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
  const output = new Float32Array(total);
  let offset = 0;
  chunks.forEach((chunk) => {
    output.set(chunk, offset);
    offset += chunk.length;
  });
  return output;
}

function downsampleAudio(input, sourceRate, targetRate) {
  if (!input.length || sourceRate === targetRate) return input;
  const ratio = sourceRate / targetRate;
  const length = Math.round(input.length / ratio);
  const output = new Float32Array(length);
  for (let i = 0; i < length; i += 1) {
    const start = Math.floor(i * ratio);
    const end = Math.min(input.length, Math.floor((i + 1) * ratio));
    let sum = 0;
    let count = 0;
    for (let j = start; j < end; j += 1) {
      sum += input[j];
      count += 1;
    }
    output[i] = count ? sum / count : 0;
  }
  return output;
}

function encodeWav(samples, sampleRate) {
  const bytesPerSample = 2;
  const blockAlign = bytesPerSample;
  const buffer = new ArrayBuffer(44 + samples.length * bytesPerSample);
  const view = new DataView(buffer);

  writeAscii(view, 0, 'RIFF');
  view.setUint32(4, 36 + samples.length * bytesPerSample, true);
  writeAscii(view, 8, 'WAVE');
  writeAscii(view, 12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * blockAlign, true);
  view.setUint16(32, blockAlign, true);
  view.setUint16(34, 16, true);
  writeAscii(view, 36, 'data');
  view.setUint32(40, samples.length * bytesPerSample, true);

  let offset = 44;
  for (let i = 0; i < samples.length; i += 1) {
    const value = Math.max(-1, Math.min(1, samples[i]));
    view.setInt16(offset, value < 0 ? value * 0x8000 : value * 0x7fff, true);
    offset += bytesPerSample;
  }

  return new Blob([buffer], { type: 'audio/wav' });
}

function writeAscii(view, offset, text) {
  for (let i = 0; i < text.length; i += 1) {
    view.setUint8(offset + i, text.charCodeAt(i));
  }
}
