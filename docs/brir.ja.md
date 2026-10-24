# Measured BRIR

## 今回できること

Spatial Lab の **4 · Measured BRIR** で、実測された部屋のバイノーラル応答を読み込み、各入力スピーカーチャンネルを左右の耳へ畳み込みます。HRIRに重ねる処理ではありません。BRIR中はSteam Audio HRTF、Directの方向フィルター、追加のEarly reflectionsを使用しません。

- 通常の音声ファイル・ディスク・ISOのチャンネル音声（2ch、5.1、7.1など）。入力チャンネルすべての対応を検証します。
- メイン画面の再生、一時停止、シーク、MixerのGain / Mute / Solo / 位相、Masterに対応します。
- 同じ処理を使う2ch FLAC保存とfloat WAV保存。FLACでは残響末尾も保存し、過大ピークは既存の検査で拒否します。自動ノーマライズは行いません。
- 同じ区間・固定した正面向きのPure HRIRとBRIRを、LUFSに基づく一定ゲインの減衰だけで比較できます。BRIR比較の補正上限は30 dBです。
- 部屋名・対応表・測定ファイルSHA-256・元サンプルレート・変換遅延を設定JSONへ記録します。音源自体と測定ファイルを変更しません。

起動時は従来通り **2ch Stereo** です。BRIRを適用しなければ通常の音は変わりません。Spatial Labで選択する独立した再生方式です。

## 操作

1. メイン画面で音源を選び、Spatial Lab の **Measured BRIR** を開きます。
2. **Add stereo WAV / SOFA…** で測定応答を追加します。SOFAの同じファイルから複数の測定を使う場合は **Duplicate route** を使います。
3. 表のSpeaker codeを `FL FR FC SL SR BL BR BC LFE` から指定します。入力音声にあるチャンネルは全て必要です。並び・名前・チャンネル数から対応を推測しません。
4. WAVのM indexは0です。SOFAは測定Mの**0始まり**の番号を指定します。Left ear / Right earは、ステレオ応答のどちらが左耳／右耳かを0・1で指定します。通常は0・1ですが、測定資料で確認してください。
5. 測定時のスピーカー位置、頭部の正面向き、左右の耳を確認してチェックを入れます。実測LFEがない場合だけ **dry LFE** を明示的に許可できます。この場合LFEは左右同量の乾いた音で、測定された部屋の距離感は付きません。LFE gainは引き続き有効です。
6. **Validate and apply BRIR** を押します。検証中は元の再生を維持し、適用成功時に同じ位置で処理を切り替えて再開します。検証失敗時も元の再生を維持します。一時停止中は自動再生しません。止めていた場合はメイン画面の **Play** で再生します。
7. 通常へ戻す場合は **Return to Pure HRIR**、またはメイン画面のPreview Audioで2ch Stereo / Direct / HRTFを選びます。

**Save profile…** のJSONと測定ファイルは別です。JSONだけでは測定データを持ち運べません。測定ファイルを移動・変更したら再検証してください。最後の対応表と適用設定はディスクの曲名情報から独立して保存します。

### A/B

**Prepare HRIR / BRIR A/B** で比較ページへ移動します。Aは選択したHRIR、Bは対応表のBRIRです。頭の向きは0°、追加反射は両方OFFです。Startと5～60秒のLengthを指定してPrepare A/Bを押し、Play A / Play Bで同じ区間を聴きます。測定値と補正ゲインが表示されます。沈黙や補正上限超過では一致済みと表示しません。

室内応答の違いによる音色・距離・残響は残します。音量一致で好みや定位の個人差がなくなるわけではありません。比較は新しい区間からのゼロ状態で、区間前から続く残響は含みません。

### 保存

- メイン画面でBRIRを適用し、**Render headphone / reflection settings to stereo FLAC** を選んでSave FLACを実行します。ADMの5.1 presentationではこのレンダリング保存が使われます。
- Spatial LabのCompare / Exportで **Use loaded Measured BRIR for float WAV export** を選ぶと、区間または選択曲全体を48 kHz float WAVへ保存できます。頭の向きは0°です。
- FLACは既存のCD音質16-bit / 44.1 kHzまたは24-bit / 48 kHzのバイノーラル保存です。BRIRの処理サンプルレートは48 kHzです。
- 同名の`.render.json`に測定ファイルとハッシュ、耳・スピーカー対応、音量、DSP版を保存します。端末のWindows音量やデバイス起動用のフェードは含みません。
- 曲ごとの保存では各曲でDSPを初期化し、残響末尾を追加します。アルバム全体を一つの畳み込み履歴で処理したギャップレス保存ではありません。

## 入力データと制限

| 形式 | 対応範囲 |
|---|---|
| WAV | 左右2耳のRIFF、PCM16/24/32またはfloat32。8～192 kHz |
| SOFA | FIRの `M×2×N`、左右の耳とMを明示指定。SamplingRateは1値・hertz、Delayは`1×2`または`M×2` |
| 応答長 | 元データの追加遅延を含め最大6秒、ファイル最大512 MiB。長い応答を黙って切りません |
| Data.Delay | 0以上の整数サンプルを保持。小数遅延は未対応のため拒否し、削除／丸めはしません |
| 対象外 | FIR-E / 多Emitter、TF、独自形式、移動オブジェクト、動的な頭部回転、Hybrid、反射成分分離 |

非48 kHzの応答は窓付きsincで変換し、IRの伝達ゲインを維持するためレート比を補正します。先頭・末尾のフィルター応答を切らないため、**そのプロファイルの全ルートへ共通4 ms（192フレーム）**の変換遅延を加えます。元の耳間差・測定遅延は保持し、追加量をJSONへ記録します。全て48 kHzならこの変換遅延はありません。畳み込み自体の追加ブロック遅延は0です。

測定されたスピーカー位置が実際の音の方向を決めます。Headphonesの配置図はチャンネル名の概略図で、測定室の厳密な位置を示しません。BRIR中は角度・頭部回転を変更して測定室を動かすことはできません。

Atmosでは先に **5.1 presentation** を選んで適用してください。Objectsへ切り替えるとPure HRTFへ戻します。スピーカー用BRIRを移動するオブジェクトへ流用しません。通常の2chスピーカー出力にはBRIRを適用せず、ヘッドフォン用の独立モードです。

## 測定データについて

データは同梱していません。自分で利用条件と測定配置を確認して用意してください。

- [IEM Production Studio](https://opendata.iem.at/projects/binauralroomresponses/) と [付属ReadME](https://phaidra.kug.ac.at/api/object/o:76742/comp/COMP000002)：KU100、左右耳のWAV、複数の頭部回転。スピーカー配置・頭部角度は資料とファイル名を照合します。今回、実ファイルのダウンロード・試聴は未検証です。
- [WDR Broadcast Studios](https://zenodo.org/records/3930833)：部屋・スピーカー位置・頭部回転に応じた複数データ。Mをそのまま360本の異なるスピーカーと解釈しないでください。全ファイルの互換性は未検証です。
- [SOFA仕様](https://www.sofaconventions.org/mediawiki/index.php/SOFA_specifications)：FIRとFIR-E、遅延の意味。
- [Steam AudioのカスタムHRTF](https://valvesoftware.github.io/steam-audio/doc/capi/guide.html#custom-hrtfs)：既存SOFA HRTF経路とは別処理です。

## 検証の範囲

検証用に作った合成IRは、計算と接続の確認専用です。実測BRIRの代用品として配布しません。独立した直接畳み込みとの比較、各FFT境界・6秒の末尾、左右、遅延、サンプルレート変換、再生とオフラインのPCM一致、FLAC・JSON、実WASAPIの無音出力、実WPF画面を確認します。

部屋の測定BRIRが実際に音楽へ与える効果、自然さ、ヘッドホンとの相性、長時間・高負荷環境での安定性は、利用者の試聴が必要です。すでに残響の多い音源では、定位やアタックが弱くなる場合もあるため、Pure HRIRとの比較を前提にしてください。
