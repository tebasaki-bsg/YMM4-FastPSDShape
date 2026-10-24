# 第三者のソフトウェア・資料に関する表記

YMM4 高速PSD図形プラグイン（YMM4FastPsdShape）の配布物（`YMM4FastPsdShape.dll`）には、第三者のソフトウェアのコードやバイナリは含まれていません。
以下は、実行時に利用するもの、開発時に使用・参考にしたものの一覧です。

## 実行時に利用するもの（配布物には含まれません）

以下はゆっくりMovieMaker4（YMM4）に同梱されているものを、YMM4 上で動作するときにそのまま利用します。本プラグインはこれらを再配布しません。

| 名称 | 提供元 | ライセンス |
|---|---|---|
| ゆっくりMovieMaker4 本体およびプラグインAPI（YukkuriMovieMaker.Plugin ほか） | 饅頭遣い | YMM4 の利用規約に従います |
| Vortice.Windows（Vortice.Direct2D1 ほか） | Amer Koleci and contributors | MIT License |
| SharpGen.Runtime | SharpGenTools contributors | MIT License |
| .NET（WPF を含む） | Microsoft | MIT License |

レイヤーツリーのフォルダのアイコンには、Windows に標準で入っているフォント「Segoe MDL2 Assets」を表示に使っています（フォントは同梱していません）。

## 同じ作者のコード

PSD の読み込み・表示状態の計算・合成の手順は、同じ作者の「YMM4 高速PSD立ち絵プラグイン（YMM4FastPsd）」（MIT License）のコードを元にしています。

## 開発時に使用・参考にしたもの

### YukkuriMovieMaker4PluginSamples
- 提供元: 饅頭遣い（manju-summoner）
- URL: https://github.com/manju-summoner/YukkuriMovieMaker4PluginSamples
- YMM4 のプラグインAPIの使い方を参考にしました。

### psd-tools
- 提供元: Kota Yamaguchi
- URL: https://github.com/psd-tools/psd-tools
- ライセンス: MIT License（Copyright (c) 2019 Kota Yamaguchi）
- 開発時に、テスト用PSD（`tests/fixtures/rules.psd`）の作成に使用しました。プラグイン本体には含まれていません。
  ソースコード一式に含まれる `tests/fixtures/make_rules_psd.py` は、実行時に psd-tools を必要とします。

### 仕様・資料
- Adobe Photoshop File Formats Specification（PSD ファイル形式の仕様）
- W3C Compositing and Blending Level 1（ブレンドモードの計算式）
