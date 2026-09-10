#!/usr/bin/env bash
printf '%s\n' "$*" >>"$FAKE_CARGO_LOG"
