// 受けた声の味付け（Web Audio）。列車無線と内線電話で共通。自分の声（送る側）にはかけない。
// ・無線: PC のクライアント（RadioVoice）と同じ。300Hz より低い音と 3kHz より高い音を削り（2段ずつ）、軽く歪ませる。雑音は混ぜない
// ・電話: 電話の帯域（300〜3400Hz）に絞り、受話器らしく 2kHz あたりを少し持ち上げ、ごく軽く歪ませる。通話中だけ回線の「サー」をうっすら
// 使い方: const fx = GokkoVoiceFx.radio(ctx, 0.6)  … 声を fx.input へつなぎ、fx.output を音量などへつなぐ。fx.setStrength(0〜1)。0 で素の声
//        const fx = GokkoVoiceFx.phone(ctx)       … 同じくつなぐ。回線の向こうから聞こえる音（呼出中の音・保留音など）も fx.input へ。
//                                                   fx.setLine(true) で回線の音（通話中だけ）、使い終わったら fx.close()
(() => {
  // ハイパス・ローパスの Q は Web Audio では dB で渡す（-3.01dB = 0.707。クライアントの Biquad と同じ形）
  const BUTTERWORTH = 20 * Math.log10(Math.SQRT1_2);
  function filter(ctx, type, freq, q = BUTTERWORTH, gain = 0) {
    const f = ctx.createBiquadFilter();
    f.type = type; f.frequency.value = freq; f.Q.value = q;
    if (gain) f.gain.value = gain;
    return f;
  }

  // tanh(x × drive) / tanh(drive) × 0.9（RadioVoice と同じ形）
  function curve(drive) {
    const n = 2048, c = new Float32Array(n), norm = Math.tanh(drive);
    for (let i = 0; i < n; i++) { const x = i / (n - 1) * 2 - 1; c[i] = Math.tanh(x * drive) / norm * 0.9; }
    return c;
  }

  function chain(ctx, nodes) {
    for (let i = 0; i < nodes.length - 1; i++) nodes[i].connect(nodes[i + 1]);
    return nodes;
  }

  // 無線（クライアントの RadioVoice と同じ: 300Hz ハイパス2段・3kHz ローパス2段・歪み 1 + 5 × 強さ）
  function radio(ctx, strength = 0.6) {
    const input = ctx.createGain(), output = ctx.createGain();
    const dry = ctx.createGain(), wet = ctx.createGain();
    const shaper = ctx.createWaveShaper(); shaper.oversample = "2x";
    chain(ctx, [input, filter(ctx, "highpass", 300), filter(ctx, "highpass", 300), filter(ctx, "lowpass", 3000), filter(ctx, "lowpass", 3000), shaper, wet, output]);
    input.connect(dry); dry.connect(output);
    let current = -1;
    function setStrength(s) {
      s = Math.min(1, Math.max(0, Number(s) || 0));
      if (s === current) return;
      current = s;
      const on = s > 0;
      dry.gain.value = on ? 0 : 1;
      wet.gain.value = on ? 1 : 0;
      if (on) shaper.curve = curve(1 + 5 * s);
    }
    setStrength(strength);
    return { input, output, setStrength, get strength() { return current; } };
  }

  let noiseBuf = null;
  function noise(ctx) {
    if (noiseBuf && noiseBuf.sampleRate === ctx.sampleRate) return noiseBuf;
    const n = ctx.sampleRate * 2;
    noiseBuf = ctx.createBuffer(1, n, ctx.sampleRate);
    const d = noiseBuf.getChannelData(0);
    for (let i = 0; i < n; i++) d[i] = Math.random() * 2 - 1;
    return noiseBuf;
  }

  // 電話（300〜3400Hz。受話器の 2kHz あたりの持ち上がり。歪みはごく軽く）
  function phone(ctx) {
    const input = ctx.createGain(), output = ctx.createGain();
    const shaper = ctx.createWaveShaper(); shaper.oversample = "2x"; shaper.curve = curve(1.8);
    chain(ctx, [input, filter(ctx, "highpass", 300), filter(ctx, "highpass", 300), filter(ctx, "peaking", 2000, 0.9, 4),
      filter(ctx, "lowpass", 3400), filter(ctx, "lowpass", 3400), shaper, output]);
    // 回線の「サー」（通話中だけ。とても小さく）
    const hiss = ctx.createBufferSource(); hiss.buffer = noise(ctx); hiss.loop = true;
    const hissGain = ctx.createGain(); hissGain.gain.value = 0;
    hiss.connect(hissGain); hissGain.connect(input);
    hiss.start();
    function setLine(on) {
      const t = ctx.currentTime;
      hissGain.gain.cancelScheduledValues(t);
      hissGain.gain.setTargetAtTime(on ? 0.006 : 0, t, 0.05);
    }
    function close() { try { hiss.stop(); } catch (e) {} try { output.disconnect(); } catch (e) {} }
    return { input, output, setLine, close };
  }

  window.GokkoVoiceFx = { radio, phone };
})();
