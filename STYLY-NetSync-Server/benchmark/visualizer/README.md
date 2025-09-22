# Visualizer

Lightweight Prometheus + Grafana bundle for monitoring STYLY NetSync Locust runs.

[日本語の説明はこちら](#日本語)

## Overview
- **Prometheus** scrapes metrics from the `locust-exporter` every 5 seconds using `visualizer/prometheus.yml` and stores them for time-series queries.
- **Grafana** loads a provisioned Prometheus datasource plus the `NetSync Locust Dashboard` from `visualizer/grafana/`, giving ready-made response time and RPS charts.
- Use this stack to watch Locust load runs in real time, explore raw PromQL, and share dashboards with teammates.

## Usage
1. Change to the benchmark directory.

   ```bash
   cd benchmark
   ```

2. Start the monitoring stack.

   ```bash
   docker compose up -d
   ```

3. Open the Locust UI at [http://localhost:8089](http://localhost:8089) and start a run so metrics flow to Prometheus.
4. Inspect Prometheus targets or run ad-hoc queries at [http://localhost:9090](http://localhost:9090).
5. View the pre-provisioned **NetSync Locust Dashboard** in Grafana at [http://localhost:3000](http://localhost:3000); anonymous admin access is enabled for quick inspection.
6. Tear down the stack when finished.

   ```bash
   docker compose down
   ```

---

## 日本語

軽量な Prometheus と Grafana のバンドルで、STYLY NetSync の Locust 計測を可視化します。

### 概要
- **Prometheus** は `visualizer/prometheus.yml` の設定で `locust-exporter` から 5 秒間隔でメトリクスを取得し、時系列データとして保存します。
- **Grafana** は Prometheus データソースと `visualizer/grafana/` 内の **NetSync Locust Dashboard** を自動読み込みし、応答時間と RPS のチャートをすぐに表示します。
- このスタックを使うと、Locust のロードテストをリアルタイムで観測しつつ、PromQL の生クエリやダッシュボード共有が行えます。

### 使い方
1. リポジトリのルートから benchmark ディレクトリに移動します。

   ```bash
   cd benchmark
   ```

2. 監視スタックを起動します。

   ```bash
   docker compose up -d
   ```

3. [http://localhost:8089](http://localhost:8089) の Locust UI を開き、テストを開始して Prometheus にメトリクスを流します。
4. [http://localhost:9090](http://localhost:9090) で Prometheus のターゲット確認やアドホックなクエリを実行します。
5. [http://localhost:3000](http://localhost:3000) の Grafana で事前 provision 済みの **NetSync Locust Dashboard** を確認します。匿名の管理者アクセスが有効です。
6. 作業が終わったらスタックを停止します。

   ```bash
   docker compose down
   ```
