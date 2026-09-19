# 取り込んでいる他者のコードについて

このプラグインには、次のコードを取り込んでいます。

---

## vnavmesh

**場所**: `Vendor/vnavmesh/`（約 7,600 行）

**用途**: 経路探索と移動。地形データ（navmesh）の生成と、そこを辿る処理。

FF14 向けの経路探索プラグイン。外部プラグインとして使うのではなく、
ソースを取り込んでいます。

**取り込んだ理由**

魔紋（宝物庫）は、区画が変わってもエリア番号（TerritoryType）が 1209 のまま変わりません。
そのため外部プラグインとしての vnavmesh には「地形を作り直すべきか」を判断できず、
前の区画の地形を使い続けて壁に向かって走る、という状態になりました。

取り込むことで、区画の変化を検知した時点でこちらから作り直させられるようになりました。

**手を入れた点**

- 描画・デバッグ画面・SharpDX 依存を除いています（周回には不要なため）
- 地形の置き場を分けています（外部版の vnavmesh と混ざらないようにするため）
- 複数クライアントが同じフォルダへ同時に書く衝突を直しています
- 古い経路探索の結果を捨てる仕組み（世代番号）を足しています

---

## DotRecast

**場所**: `Vendor/DotRecast/`（約 30,900 行）

**用途**: vnavmesh が内部で使う経路探索のライブラリ。

**ライセンス**: zlib

```
Copyright (c) 2009-2010 Mikko Mononen memon@inside.org
recast4j copyright (c) 2015-2019 Piotr Piastucki piotr@jtilia.org
DotRecast Copyright (c) 2023 Choi Ikpil ikpil@naver.com

This software is provided 'as-is', without any express or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
```

zlib は取り込みと改変を認めており、上の表記を残すことが条件です。
各ソースファイルの先頭にある著作権表示は、そのまま残しています。

**取り込んだ範囲**: Core / Detour / Detour.Extras / Recast の4つ。
描画（Render）とデバッグ用は含めていません。

---

## このプラグイン全体のライセンス

**AGPL-3.0**（`LICENSE` を参照）

取り込んだ vnavmesh に合わせています。
AGPL は「取り込んだ側も同じライセンスで公開する」ことを求めるため、
このプラグイン全体が AGPL-3.0 になります。

自作部分（`AutoTreasure/` 以下、約 15,800 行）も AGPL-3.0 として公開します。
