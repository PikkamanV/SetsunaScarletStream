# SetsunaScarletStream
HLSで配信されてるのを録画するやつ

## 使い方
1. `cp config.json.example config.json` で設定例をコピー
2. config.json 内で録画したい時間を指定
3. docker-compose.yml でホストマシン側の保存先を指定
4. `docker compose up -d --build` で起動
