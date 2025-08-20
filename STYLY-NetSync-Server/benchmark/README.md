# STYLY NetSync ベンチマークツール

**[English version available below](#styly-netsync-benchmark-tools)**

このディレクトリには、STYLY NetSyncサーバーの負荷テスト用のLocustベースのベンチマークツールが含まれています。このベンチマークは、transform同期とRPC呼び出しなど、典型的なマルチプレイヤーアクションを実行する複数のVR/MRクライアントをシミュレートします。

## 機能

- **Transform同期**: 設定可能なレート（デフォルト：50Hz）で円形パターンで移動するVR/MRユーザーをシミュレート
- **RPC呼び出し**: ブロードキャスト、サーバー、クライアント間のリモートプロシージャ呼び出しをテスト
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

# またはrequirements.txtから直接インストール
uv pip install -r requirements.txt
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
| `STYLY_SERVER_ADDRESS` | サーバーアドレス |
| `STYLY_DEALER_PORT` | DEALERソケットポート |
| `STYLY_SUB_PORT` | SUBソケットポート |
| `STYLY_ROOM_ID` | テスト用ルームID |
| `STYLY_TRANSFORM_RATE` | Transform更新レート（Hz） |
| `STYLY_RPC_INTERVAL` | RPC送信間隔（秒） |
| `STYLY_MOVEMENT_RADIUS` | シミュレーション用移動半径 |
| `STYLY_MOVEMENT_SPEED` | 移動速度倍率 |
| `STYLY_DETAILED_LOGGING` | 詳細ログを有効化 |

**注意**: デフォルト値については `benchmark_config.py` を参照してください。設定値は定期的に調整される場合があります。

### 設定例

```bash
# 高頻度テストと詳細ログ
export STYLY_TRANSFORM_RATE=100.0
export STYLY_RPC_INTERVAL=5.0
export STYLY_DETAILED_LOGGING=true

uv run locust -f locustfile.py --host=tcp://localhost:5555
```

## ログレベル制御

ログ出力レベルはLocustの`--loglevel`オプションで制御できます：

```bash
# デバッグログを表示
uv run locust -f locustfile.py --host=tcp://localhost:5555 --loglevel=DEBUG
```

## ベンチマークシナリオ

### 1. 基本負荷テスト
```bash
# 10ユーザー、段階的増加
uv run locust -f locustfile.py --host=tcp://localhost:5555 --headless -u 10 -r 2 -t 180s
```

### 2. 高負荷テスト
```bash
# 100ユーザー、積極的な増加
uv run locust -f locustfile.py --host=tcp://localhost:5555 --headless -u 100 -r 10 -t 600s
```

### 3. ストレステスト
```bash
# 500ユーザー、最大負荷
uv run locust -f locustfile.py --host=tcp://localhost:5555 --headless -u 500 -r 25 -t 900s
```

### 4. 耐久テスト
```bash
# 50ユーザーで1時間
uv run locust -f locustfile.py --host=tcp://localhost:5555 --headless -u 50 -r 5 -t 3600s
```

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

### 分散テスト

複数マシンでの実行：

```bash
# マスターノード
uv run locust -f locustfile.py --host=tcp://server:5555 --master

# ワーカーノード
uv run locust -f locustfile.py --host=tcp://server:5555 --worker --master-host=master-ip
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

### パフォーマンスチューニング

1. **システム制限**
   ```bash
   # ファイルディスクリプタ制限を増加
   ulimit -n 65536
   
   # 現在の制限を確認
   ulimit -a
   ```

2. **ネットワーク最適化**
   ```bash
   # ネットワークバッファサイズを増加
   echo 'net.core.rmem_max = 134217728' >> /etc/sysctl.conf
   echo 'net.core.wmem_max = 134217728' >> /etc/sysctl.conf
   sysctl -p
   ```

3. **Python最適化**
   ```bash
   # より良いパフォーマンスのためにPyPyを使用（オプション）
   pypy3 -m pip install -r requirements.txt
   pypy3 -m locust -f locustfile.py
   ```

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

### クライアント動作の拡張

```python
# styly_client.py内で
class STYLYNetSyncClient:
    def custom_behavior(self):
        # カスタムクライアント動作を追加
        pass
```

---

# STYLY NetSync Benchmark Tools

This directory contains Locust-based benchmarking tools for load testing the STYLY NetSync server. The benchmark simulates multiple VR/MR clients performing typical multiplayer actions including transform synchronization, network variables, and RPC calls.

## Features

- **Transform Synchronization**: Simulates VR/MR users moving in circular patterns at configurable rates (default: 50Hz)
- **Network Variables**: Tests global and client-specific variable synchronization
- **RPC Calls**: Tests broadcast, server, and client-to-client remote procedure calls
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

# Or install directly from requirements.txt
uv pip install -r requirements.txt
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
| `STYLY_RPC_INTERVAL` | RPC send interval (seconds) |
| `STYLY_MOVEMENT_RADIUS` | Movement radius for simulation |
| `STYLY_MOVEMENT_SPEED` | Movement speed multiplier |
| `STYLY_DETAILED_LOGGING` | Enable detailed logging |

**Note**: For default values, please refer to `benchmark_config.py`. Configuration values may be adjusted periodically.

### Example Configuration

```bash
# High-frequency test with detailed logging
export STYLY_TRANSFORM_RATE=100.0
export STYLY_RPC_INTERVAL=5.0
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

### 1. Basic Load Test
```bash
# 10 users, gradual ramp-up
uv run locust -f locustfile.py --host=tcp://localhost:5555 --headless -u 10 -r 2 -t 180s
```

### 2. High Load Test
```bash
# 100 users, aggressive ramp-up
uv run locust -f locustfile.py --host=tcp://localhost:5555 --headless -u 100 -r 10 -t 600s
```

### 3. Stress Test
```bash
# 500 users, maximum load
uv run locust -f locustfile.py --host=tcp://localhost:5555 --headless -u 500 -r 25 -t 900s
```

### 4. Endurance Test
```bash
# 50 users for 1 hour
uv run locust -f locustfile.py --host=tcp://localhost:5555 --headless -u 50 -r 5 -t 3600s
```

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

### Distributed Testing

Run across multiple machines:

```bash
# Master node
uv run locust -f locustfile.py --host=tcp://server:5555 --master

# Worker nodes
uv run locust -f locustfile.py --host=tcp://server:5555 --worker --master-host=master-ip
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

### Performance Tuning

1. **System Limits**
   ```bash
   # Increase file descriptor limit
   ulimit -n 65536
   
   # Check current limits
   ulimit -a
   ```

2. **Network Optimization**
   ```bash
   # Increase network buffer sizes
   echo 'net.core.rmem_max = 134217728' >> /etc/sysctl.conf
   echo 'net.core.wmem_max = 134217728' >> /etc/sysctl.conf
   sysctl -p
   ```

3. **Python Optimization**
   ```bash
   # Use PyPy for better performance (optional)
   pypy3 -m pip install -r requirements.txt
   pypy3 -m locust -f locustfile.py
   ```

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

### Extending Client Behavior

```python
# In styly_client.py
class STYLYNetSyncClient:
    def custom_behavior(self):
        # Add your custom client behavior
        pass
```
