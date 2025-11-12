#!/bin/bash

cd STYLY-NetSync-Server/

# Upgrade pip to avoid version warnings
pip install --upgrade pip

# Install development dependencies
pip install -e ".[dev]"

# Install in development mode
pip install -e .
