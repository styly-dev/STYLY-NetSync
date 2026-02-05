# STYLY NetSync Benchmark Tools

**[日本語はこちら](#styly-netsync-ベンチマークツール)**


This directory contains Locust-based benchmarking tools for load testing the STYLY NetSync server. The benchmark simulates multiple VR/MR clients performing typical multiplayer actions including transform synchronization, network variables, and RPC calls.

## Features

- **Transform Synchronization**: Simulates VR/MR users moving in circular patterns at configurable rates
- **Transform Payload Comparison**: Logs one-shot `legacy_v2` estimate vs `protocol_v3` actual payload size and reduction rate
- **Network Variables**: Tests global and client-specific variable synchronization
- **RPC Calls**: Tests remote procedure calls
- **Comprehensive Metrics**: Latency, throughput, packet loss, and connection stability measurements
- **Real-time Monitoring**: Locust web UI with custom STYLY-specific metrics
- **Flexible Configuration**: Environment variable-based configuration
- **Independent Dependencies**: No impact on main project dependencies

## Technical Details

Locust is gevent-based rather than asyncio-based, so PyZMQ is configured to run in green mode (`zmq.green`) for compatibility with ZeroMQ. This allows cooperative operation with Locust's event loop and achieves high concurrent performance.

## Quick Start

### 1. Setup Environment

```bash
# Navigate to benchmark directory
cd benchmark

# Install dependencies with uv (automatically creates virtual environment)
uv sync
```

### 2. Start STYLY NetSync Server

In a separate terminal:

```bash
# From project root
uv run styly-netsync-server
```

### 3. Run Benchmark

#### Basic Web UI Mode
```bash
uv run locust -f locustfile.py --host=tcp://localhost:5555
```

Then open http://localhost:8089 in your browser to configure and start the test.

#### Headless Mode (for CI/CD)
```bash
# 50 users, spawn 5 per second, run for 5 minutes
uv run locust -f locustfile.py --host=tcp://localhost:5555 --headless -u 50 -r 5 -t 300s
```

#### With Custom Configuration
```bash
# Custom server and room
STYLY_SERVER_ADDRESS=192.168.1.100 STYLY_ROOM_ID=load_test uv run locust -f locustfile.py --host=tcp://192.168.1.100:5555
```

## Configuration

Configure the benchmark using environment variables:

| Variable | Description |
|----------|-------------|
| `STYLY_SERVER_ADDRESS` | Server address |
| `STYLY_DEALER_PORT` | DEALER socket port |
| `STYLY_SUB_PORT` | SUB socket port |
| `STYLY_ROOM_ID` | Room ID for testing |
| `STYLY_TRANSFORM_RATE` | Transform update rate (Hz) |
| `STYLY_RPC_PER_TRANSFORMS` | Send RPC every N transforms |
| `STYLY_MOVEMENT_RADIUS` | Movement radius for simulation |
| `STYLY_MOVEMENT_SPEED` | Movement speed multiplier |
| `STYLY_DETAILED_LOGGING` | Enable detailed logging |

**Note**: For default values, please refer to `benchmark_config.py`. Configuration values may be adjusted periodically.

### Example Configuration

```bash
# High-frequency test with detailed logging
export STYLY_TRANSFORM_RATE=100.0
export STYLY_RPC_PER_TRANSFORMS=6
export STYLY_DETAILED_LOGGING=true

uv run locust -f locustfile.py --host=tcp://localhost:5555
```

## Log Level Control

Log output level can be controlled with Locust's `--loglevel` option:

```bash
# Show debug logs
uv run locust -f locustfile.py --host=tcp://localhost:5555 --loglevel=DEBUG
```

## Benchmark Scenarios

### Basic Load Test
```bash
# 10 users, gradual ramp-up
uv run locust -f locustfile.py --host=tcp://localhost:5555 --headless -u 10 -r 2 -t 180s
```

### High Load & Distributed Test
High-load runs (100+ users) require Locust's distributed mode. Define the scenario on the master process and attach one or more workers.

1. **Master node**

```bash
uv run locust -f locustfile.py --host=tcp://localhost:5555 --master -u 100 -r 10 -t 600s
```

2. **Worker nodes**

```bash
uv run locust -f - --host=tcp://localhost:5555 --worker --master-host=<master-ip>
```

Replace `<master-ip>` with your master's address and adjust the user count (`-u`) or spawn rate (`-r`) as needed.

## Metrics and Analysis

### Real-time Metrics

The benchmark provides real-time metrics through:

1. **Locust Web UI**: Standard HTTP request metrics adapted for STYLY operations
2. **Console Logs**: Detailed statistics every 10 seconds
3. **Custom Metrics**: STYLY-specific performance measurements

### Exported Data

After each test run, detailed metrics are exported to:
- `results/benchmark_results_<timestamp>.json`: Complete metrics data
- Console output: Summary statistics

### Key Metrics

- **Latency**: Average, P95, P99 response times
- **Throughput**: Messages per second, bytes per second
- **Packet Loss**: Sent vs received message ratios
- **Connection Stability**: Error rates, reconnection counts
- **Per-Message-Type Stats**: Transform, network variable, and RPC performance

## Advanced Usage

### Custom Load Shapes

Use the built-in step load shape:

```bash
uv run locust -f locustfile.py --host=tcp://localhost:5555 --shape-class=StepLoadShape
```

### Custom Command Line Options

```bash
# Enable detailed STYLY logging
uv run locust -f locustfile.py --host=tcp://localhost:5555 --styly-detailed-logging

# Export metrics to custom file
uv run locust -f locustfile.py --host=tcp://localhost:5555 --styly-export-metrics=my_results.json
```

## Troubleshooting

### Common Issues

1. **Connection Refused**
   ```
   Error: Failed to connect to server
   ```
   - Ensure STYLY NetSync server is running
   - Check server address and ports
   - Verify firewall settings

2. **High Error Rates**
   ```
   Warning: High connection error rate
   ```
   - Reduce user count or spawn rate
   - Check server resource usage
   - Increase system file descriptor limits

3. **Memory Issues**
   ```
   Error: Out of memory
   ```
   - Reduce metrics collection window size
   - Lower user count
   - Use distributed testing

## Development

### Adding Custom Metrics

```python
# In locustfile.py
from .metrics_collector import MetricsCollector

# Add custom metric collection
def custom_metric_task(self):
    start_time = time.time()
    # Your custom operation
    response_time = (time.time() - start_time) * 1000
    
    self.environment.events.request.fire(
        request_type="STYLY",
        name="custom_operation",
        response_time=response_time,
        response_length=0,
        exception=None,
        context={}
    )
```

---

# STYLY NetSync ベンチマークツール

**[English version is above](#styly-netsync-benchmark-tools)**

このディレクトリには、STYLY NetSyncサーバーの負荷テスト用のLocustベースのベンチマークツールが含まれています。このベンチマークは、transform同期とRPC呼び出しなど、典型的なマルチプレイヤーアクションを実行する複数のVR/MRクライアントをシミュレートします。

## 機能

- **Transform同期**: 設定可能なレートで円形パターンで移動するVR/MRユーザーをシミュレート
- **Transformペイロード比較**: `legacy_v2` 推定サイズと `protocol_v3` 実測サイズ、および削減率を1回ログ出力
- **RPC呼び出し**: リモートプロシージャ呼び出しをテスト
- **包括的メトリクス**: レイテンシ、スループット、パケットロス、接続安定性の測定
- **リアルタイム監視**: STYLY固有のカスタムメトリクスを持つLocust Web UI
- **柔軟な設定**: 環境変数ベースの設定
- **独立した依存関係**: メインプロジェクトの依存関係に影響なし

## 技術的詳細

LocustはasyncioではなくgeventベースのためZeroMQとの相性を考慮し、PyZMQをgreenモード（`zmq.green`）で動作させています。これによりLocustのイベントループと協調動作し、高い並行性能を実現しています。

## クイックスタート

### 1. 環境セットアップ

```bash
# benchmarkディレクトリに移動
cd benchmark

# uvで依存関係をインストール（自動で仮想環境作成）
uv sync
```

### 2. STYLY NetSyncサーバーを起動

別のターミナルで：

```bash
# プロジェクトルートから
uv run styly-netsync-server
```

### 3. ベンチマーク実行

#### 基本的なWeb UIモード
```bash
uv run locust -f locustfile.py --host=tcp://localhost:5555
```

その後、ブラウザで http://localhost:8089 を開いてテストを設定・開始します。

#### ヘッドレスモード（CI/CD用）
```bash
# 50ユーザー、毎秒5人生成、5分間実行
uv run locust -f locustfile.py --host=tcp://localhost:5555 --headless -u 50 -r 5 -t 300s
```

#### カスタム設定付き
```bash
# カスタムサーバーとルーム
STYLY_SERVER_ADDRESS=192.168.1.100 STYLY_ROOM_ID=load_test uv run locust -f locustfile.py --host=tcp://192.168.1.100:5555
```

## 設定

環境変数を使用してベンチマークを設定：

| 変数 | 説明 |
|------|------|
| `STYLY_CLIENT_TYPE` | クライアント実装タイプ (`raw_zmq` または `netsync_manager`) |
| `STYLY_SERVER_ADDRESS` | サーバーアドレス |
| `STYLY_DEALER_PORT` | DEALERソケットポート |
| `STYLY_SUB_PORT` | SUBソケットポート |
| `STYLY_ROOM_ID` | テスト用ルームID |
| `STYLY_TRANSFORM_RATE` | Transform更新レート（Hz） |
| `STYLY_RPC_PER_TRANSFORMS` | Transform N回あたりRPC 1回 |
| `STYLY_MOVEMENT_RADIUS` | シミュレーション用移動半径 |
| `STYLY_MOVEMENT_SPEED` | 移動速度倍率 |
| `STYLY_DETAILED_LOGGING` | 詳細ログを有効化 |

**注意**: デフォルト値については `benchmark_config.py` を参照してください。設定値は定期的に調整される場合があります。

### クライアント実装の選択

ベンチマークには2つのクライアント実装があります：

1. **`raw_zmq`** (デフォルト): 直接ZeroMQソケットを使用する低レベル実装
2. **`netsync_manager`**: `styly_netsync.client.net_sync_manager`クラスを使用する高レベル実装

#### 環境変数で指定
```bash
export STYLY_CLIENT_TYPE=netsync_manager
uv run locust -f locustfile.py --host=tcp://localhost:5555
```

#### コマンドライン引数で指定
```bash
uv run locust -f locustfile.py --host=tcp://localhost:5555 --styly-client-type=netsync_manager
```

### 設定例

#### 高頻度テストと詳細ログ（Raw ZMQ実装）
```bash
export STYLY_CLIENT_TYPE=raw_zmq
export STYLY_TRANSFORM_RATE=100.0
export STYLY_RPC_PER_TRANSFORMS=6
export STYLY_DETAILED_LOGGING=true

uv run locust -f locustfile.py --host=tcp://localhost:5555
```

#### NetSync Manager実装でのテスト
```bash
export STYLY_CLIENT_TYPE=netsync_manager
export STYLY_TRANSFORM_RATE=50.0

uv run locust -f locustfile.py --host=tcp://localhost:5555
```

## ログレベル制御

ログ出力レベルはLocustの`--loglevel`オプションで制御できます：

```bash
# デバッグログを表示
uv run locust -f locustfile.py --host=tcp://localhost:5555 --loglevel=DEBUG
```

## ベンチマークシナリオ

### 基本負荷テスト
```bash
# 10ユーザー、段階的増加
uv run locust -f locustfile.py --host=tcp://localhost:5555 --headless -u 10 -r 2 -t 180s
```

### 高負荷・分散テスト
高負荷シナリオ（例: 100ユーザー以上）では Locust の分散実行が必須です。マスターノードで条件を指定し、複数ワーカーノードを接続してください。

1. **マスターノード**

```bash
uv run locust -f locustfile.py --host=tcp://localhost:5555 --master -u 100 -r 10 -t 600s
```

2. **ワーカーノード**

```bash
uv run locust -f - --host=tcp://localhost:5555 --worker --master-host=<master-ip>
```

`<master-ip>` はマスターノードのアドレスに置き換えてください。必要に応じてユーザー数 (`-u`) や生成レート (`-r`) を調整します。

## メトリクスと分析

### リアルタイムメトリクス

ベンチマークは以下を通じてリアルタイムメトリクスを提供：

1. **Locust Web UI**: STYLY操作に適応した標準HTTPリクエストメトリクス
2. **コンソールログ**: 10秒ごとの詳細統計
3. **カスタムメトリクス**: STYLY固有のパフォーマンス測定

### エクスポートデータ

各テスト実行後、詳細メトリクスが以下にエクスポート：
- `results/benchmark_results_<timestamp>.json`: 完全なメトリクスデータ
- コンソール出力: 要約統計

### 主要メトリクス

- **レイテンシ**: 平均、P95、P99応答時間
- **スループット**: 秒間メッセージ数、秒間バイト数
- **パケットロス**: 送信対受信メッセージ比率
- **接続安定性**: エラー率、再接続回数
- **メッセージタイプ別統計**: Transform、ネットワーク変数、RPCパフォーマンス

## 高度な使用方法

### カスタム負荷パターン

組み込みのステップ負荷パターンを使用：

```bash
uv run locust -f locustfile.py --host=tcp://localhost:5555 --shape-class=StepLoadShape
```

### カスタムコマンドラインオプション

```bash
# 詳細STYLYログを有効化
uv run locust -f locustfile.py --host=tcp://localhost:5555 --styly-detailed-logging

# カスタムファイルにメトリクスをエクスポート
uv run locust -f locustfile.py --host=tcp://localhost:5555 --styly-export-metrics=my_results.json
```

## トラブルシューティング

### よくある問題

1. **接続拒否**
   ```
   Error: Failed to connect to server
   ```
   - STYLY NetSyncサーバーが実行中であることを確認
   - サーバーアドレスとポートを確認
   - ファイアウォール設定を確認

2. **高エラー率**
   ```
   Warning: High connection error rate
   ```
   - ユーザー数またはスポーン率を減らす
   - サーバーリソース使用量を確認
   - システムファイルディスクリプタ制限を増加

3. **メモリ問題**
   ```
   Error: Out of memory
   ```
   - メトリクス収集ウィンドウサイズを減らす
   - ユーザー数を減らす
   - 分散テストを使用

## 開発

### カスタムメトリクスの追加

```python
# locustfile.py内で
from .metrics_collector import MetricsCollector

# カスタムメトリクス収集を追加
def custom_metric_task(self):
    start_time = time.time()
    # カスタム操作
    response_time = (time.time() - start_time) * 1000
    
    self.environment.events.request.fire(
        request_type="STYLY",
        name="custom_operation",
        response_time=response_time,
        response_length=0,
        exception=None,
        context={}
    )
```
