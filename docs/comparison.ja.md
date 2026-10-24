# Compare / Export — A/B比較の使い方

## 3つのボタンの役割

| ボタン | 動作 |
|---|---|
| Prepare A/B | 選択した曲の区間をA・Bそれぞれの設定で計算し、音量を測定します。この時点では音は出ません |
| Play A from start | 準備済みのAを、上のStartで指定した位置から試聴します |
| Play B from start | 準備済みのBを、同じStart位置から試聴します |
| Stop A/B | 比較の試聴を止め、メイン画面の操作へ戻ります。準備済みデータは再試聴できます |

Startは**選択した曲の先頭からの秒数**、Lengthは比較する長さ（5～60秒）です。曲全体を再生するボタンではありません。曲末を超える部分は実際の曲末までになります。

## HRIRとBRIRを比較する

1. メイン画面で音源と曲を選びます。TrueHD Atmosの場合は、メイン画面の **Enable ADM playback** で随時再生を有効にします。
2. **4 · Measured BRIR** の **Open BRIR profile…** で、たとえば `D1-48k-5.1.brir.json` を読み込みます。
3. **2 · Compare / Export** で **HRIR vs BRIR** にチェックします。**Validate and apply BRIR** を使った場合は、この比較が自動選択されます。
4. Atmosの随時再生では、**比較画面の上部**にある **Use 5.1 for HRIR / BRIR** を押し、**5.1 presentation · headphones** に切り替えます。AのHRIRを選びます。最初は **Steam Audio built-in** で構いません。BRIR比較中は、使用しないHRIR B欄が無効になります。頭の向きは0°です。
5. Startを `0`、Lengthを `15` などにして **Prepare A/B** を押します。比較のため、メインの通常再生はいったん一時停止します。
6. ボタン直下の **Prepared** 表示を確認し、**Play A**、**Play B** で聴きます。Aの途中でもBを押せます。先の試聴を止め、選んだ版を比較区間の先頭から再生します。

準備中は進捗バー、試聴中はA/B・現在時刻・区間の長さが表示されます。試聴は固定した設定の比較データです。メインの再生位置は保持します。メイン画面のPlayは、Stop A/Bの後に操作できます。

## HRIR同士を比較する

**HRIR vs room** と **HRIR vs BRIR** の両方をOFFにし、A・BのHRIRを選びます。たとえばAを内蔵、Bを登録した互換SOFAにします。

**AとBが両方ともSteam Audio built-inなら、同じ音になります。** この場合、準備完了時にSAME HRIRと表示します。別のHRIR Bを選んで再びPrepareするか、HRIR vs BRIRを選んでください。

## 初期反射を比較する

**HRIR vs room** は、A＝反射なし、B＝Aと同じHRIRに現在の反射設定を加えた音です。3番のHRIR / ReflectionsでStudioやListening Roomを選んでから比較します。HRIR vs BRIRとは同時選択できません。

## ボタンが使えない場合

- **Source presentation** は比較画面の上部に常に表示します。メイン画面にも **Compare source presentation** があります。両方とも同じ音源モードを操作し、切り替えても曲と再生位置を保持します。
- 通常のWAV/FLACなどでは、そのファイルの元のチャンネル配置を使うため、この選択欄は無効です。TrueHD Atmosを開いて **Enable ADM playback** を押すと有効になります。書き出し済みADMはObjectsのシーンを持つだけで、別の5.1音声を含むとは限りません。その場合は元のTrueHD Atmos、または5.1/7.1などの音声ファイルを開いてください。
- Play A / Bは、Prepareが正常終了してから有効になります。
- 準備の失敗理由は**ボタン直下**に赤字で表示します。音源未選択、Startが曲末以降、非対応SOFA、BRIR未読込などを確認してください。
- 曲、区間、A/B、比較方式などを変更した場合は、**もう一度Prepare**してください。古い比較データを誤って再生しないようにしています。
- Atmosの **Stereo presentation** はHRIR / BRIRを迂回する音源比較用です。この画面の音響処理比較には5.1、HRIRだけならObjectsも使えます。
- 選択区間が無音の場合や、準備時の音量・Mixer Mute / Soloで無音になった場合は、その旨を表示します。メイン側の設定を確認してからPrepareしてください。
- 音量一致は一定ゲインの減衰だけで行います。音量測定できない無音や補正範囲外では「Loudness matched」とは表示しません。

準備時に確定した音量・ミキサー設定を使います。音量やミキサーを変えて結果を比較したい場合も、再びPrepareしてください。
