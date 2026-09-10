impl StubBridgeService {
    pub fn get_logs(&self, request: proto::LogsRequest) -> proto::LogsResult {
        if request.limit == 0 {
            return proto::LogsResult {
                result: Some(proto::logs_result::Result::Error(error(
                    proto::BridgeErrorCode::InvalidQueryFilter,
                    "logs.limit must be greater than zero.",
                    &[detail(
                        "limit",
                        "0",
                        "Use a positive limit when querying bridge logs.",
                    )],
                ))),
            };
        }

        let minimum_level = proto::LogLevel::try_from(request.minimum_level)
            .ok()
            .unwrap_or(proto::LogLevel::Unspecified);
        let target_filter = request.target_filter.to_ascii_lowercase();
        let after_cursor = request.after_cursor;

        let entries = stub_log_entries()
            .into_iter()
            .filter(|entry| {
                matches_minimum_level(enum_value(entry.level), minimum_level)
                    && (target_filter.is_empty()
                        || entry
                            .target
                            .to_ascii_lowercase()
                            .contains(target_filter.as_str()))
            })
            .collect::<Vec<_>>();
        let current_cursor = entries.last().map(|entry| entry.cursor).unwrap_or_default();
        let entries = if after_cursor == 0 {
            let skip = entries.len().saturating_sub(request.limit as usize);
            entries.into_iter().skip(skip).collect::<Vec<_>>()
        } else {
            entries
                .into_iter()
                .filter(|entry| entry.cursor > after_cursor)
                .take(request.limit as usize)
                .collect::<Vec<_>>()
        };
        let next_cursor = entries
            .last()
            .map(|entry| entry.cursor)
            .unwrap_or(current_cursor);

        proto::LogsResult {
            result: Some(proto::logs_result::Result::Success(proto::LogsResponse {
                source: proto::DataSource::Stub as i32,
                provisional: true,
                entries,
                next_cursor,
            })),
        }
    }

    pub fn get_debug_status(
        &self,
        _request: proto::DebugStatusRequest,
    ) -> proto::DebugStatusResult {
        let state = self.debug_state.lock().expect("debug state lock");
        proto::DebugStatusResult {
            result: Some(proto::debug_status_result::Result::Success(
                stub_debug_status(&state.breakpoints),
            )),
        }
    }

    pub fn start_debug_session(
        &self,
        _request: proto::DebugSessionStartRequest,
    ) -> proto::DebugSessionStartResult {
        let state = self.debug_state.lock().expect("debug state lock");
        proto::DebugSessionStartResult {
            result: Some(proto::debug_session_start_result::Result::Success(
                proto::DebugSessionStartResponse {
                    started: false,
                    session: None,
                    status: Some(stub_debug_status(&state.breakpoints)),
                    notices: vec![debug_notice(
                        "debug_session_unsupported",
                        "Debugger sessions are not supported in the stub debug bridge.",
                    )],
                },
            )),
        }
    }

    pub fn get_debug_session_status(
        &self,
        _request: proto::DebugSessionStatusRequest,
    ) -> proto::DebugSessionStatusResult {
        let state = self.debug_state.lock().expect("debug state lock");
        proto::DebugSessionStatusResult {
            result: Some(proto::debug_session_status_result::Result::Success(
                proto::DebugSessionStatusResponse {
                    found: false,
                    session: None,
                    status: Some(stub_debug_status(&state.breakpoints)),
                    notices: vec![debug_notice(
                        "debug_session_unsupported",
                        "Debugger sessions are not supported in the stub debug bridge.",
                    )],
                },
            )),
        }
    }

    pub fn end_debug_session(
        &self,
        request: proto::DebugSessionEndRequest,
    ) -> proto::DebugSessionEndResult {
        let state = self.debug_state.lock().expect("debug state lock");
        proto::DebugSessionEndResult {
            result: Some(proto::debug_session_end_result::Result::Success(
                proto::DebugSessionEndResponse {
                    ended: false,
                    id: request.id,
                    status: Some(stub_debug_status(&state.breakpoints)),
                    notices: vec![debug_notice(
                        "debug_session_unsupported",
                        "Debugger sessions are not supported in the stub debug bridge.",
                    )],
                },
            )),
        }
    }

    pub fn pause_debug(&self, _request: proto::DebugPauseRequest) -> proto::DebugPauseResult {
        let state = self.debug_state.lock().expect("debug state lock");
        proto::DebugPauseResult {
            result: Some(proto::debug_pause_result::Result::Success(
                proto::DebugPauseResponse {
                    applied: false,
                    status: Some(stub_debug_status(&state.breakpoints)),
                    notices: vec![debug_notice(
                        "debug_pause_unsupported",
                        "Pause is not supported in the stub debug bridge.",
                    )],
                },
            )),
        }
    }

    pub fn resume_debug(&self, _request: proto::DebugResumeRequest) -> proto::DebugResumeResult {
        let state = self.debug_state.lock().expect("debug state lock");
        proto::DebugResumeResult {
            result: Some(proto::debug_resume_result::Result::Success(
                proto::DebugResumeResponse {
                    applied: false,
                    status: Some(stub_debug_status(&state.breakpoints)),
                    notices: vec![debug_notice(
                        "debug_resume_unsupported",
                        "Resume is not supported in the stub debug bridge.",
                    )],
                },
            )),
        }
    }

    pub fn step_debug(&self, request: proto::DebugStepRequest) -> proto::DebugStepResult {
        let state = self.debug_state.lock().expect("debug state lock");
        proto::DebugStepResult {
            result: Some(proto::debug_step_result::Result::Success(
                proto::DebugStepResponse {
                    applied: false,
                    status: Some(stub_debug_status(&state.breakpoints)),
                    notices: vec![debug_notice(
                        "debug_step_unsupported",
                        &format!(
                            "Step kind '{}' is not supported in the stub debug bridge.",
                            debug_step_kind_name(request.kind)
                        ),
                    )],
                },
            )),
        }
    }

    pub fn wait_debug(&self, _request: proto::DebugWaitRequest) -> proto::DebugWaitResult {
        let state = self.debug_state.lock().expect("debug state lock");
        proto::DebugWaitResult {
            result: Some(proto::debug_wait_result::Result::Success(
                proto::DebugWaitResponse {
                    completed: false,
                    timed_out: false,
                    status: Some(stub_debug_status(&state.breakpoints)),
                    notices: vec![debug_notice(
                        "debug_wait_unsupported",
                        "Debug wait is not supported in the stub debug bridge.",
                    )],
                },
            )),
        }
    }

    pub fn list_breakpoints(
        &self,
        _request: proto::DebugBreakpointListRequest,
    ) -> proto::DebugBreakpointListResult {
        let state = self.debug_state.lock().expect("debug state lock");
        proto::DebugBreakpointListResult {
            result: Some(proto::debug_breakpoint_list_result::Result::Success(
                proto::DebugBreakpointListResponse {
                    breakpoints: state.breakpoints.clone(),
                    status: Some(stub_debug_status(&state.breakpoints)),
                    notices: vec![],
                },
            )),
        }
    }

    pub fn add_breakpoint(
        &self,
        request: proto::DebugBreakpointAddRequest,
    ) -> proto::DebugBreakpointAddResult {
        if request.query_path.trim().is_empty() {
            return proto::DebugBreakpointAddResult {
                result: Some(proto::debug_breakpoint_add_result::Result::Error(error(
                    proto::BridgeErrorCode::InvalidQueryFilter,
                    "breakpoint add requires a query path.",
                    &[detail(
                        "query_path",
                        "",
                        "Provide a state query path such as screen.id or combat.turn.",
                    )],
                ))),
            };
        }

        let mut state = self.debug_state.lock().expect("debug state lock");
        state.next_breakpoint_id += 1;
        let breakpoint = proto::DebugBreakpoint {
            id: format!("bp:{}", state.next_breakpoint_id),
            name: request.name,
            query_path: request.query_path,
            predicate: request.predicate,
            enabled: true,
            provisional: true,
            kind: request.kind,
            min_hit_count: request.min_hit_count.max(1),
            hit_count: 0,
            auto_remove_on_hit: request.auto_remove_on_hit,
            last_observed_json: String::new(),
        };
        state.breakpoints.push(breakpoint.clone());

        proto::DebugBreakpointAddResult {
            result: Some(proto::debug_breakpoint_add_result::Result::Success(
                proto::DebugBreakpointAddResponse {
                    added: true,
                    breakpoint: Some(breakpoint),
                    status: Some(stub_debug_status(&state.breakpoints)),
                    notices: vec![debug_notice(
                        "debug_breakpoint_storage_only",
                        "Breakpoint storage is available, but live breakpoint evaluation is not wired in the stub bridge.",
                    )],
                },
            )),
        }
    }

    pub fn remove_breakpoint(
        &self,
        request: proto::DebugBreakpointRemoveRequest,
    ) -> proto::DebugBreakpointRemoveResult {
        if request.id.trim().is_empty() {
            return proto::DebugBreakpointRemoveResult {
                result: Some(proto::debug_breakpoint_remove_result::Result::Error(error(
                    proto::BridgeErrorCode::InvalidQueryFilter,
                    "breakpoint remove requires a breakpoint id.",
                    &[detail(
                        "id",
                        "",
                        "Use dev breakpoint list to inspect the registered breakpoint ids.",
                    )],
                ))),
            };
        }

        let mut state = self.debug_state.lock().expect("debug state lock");
        let original_len = state.breakpoints.len();
        state.breakpoints.retain(|item| item.id != request.id);
        let removed = state.breakpoints.len() != original_len;
        let notices = if removed {
            vec![]
        } else {
            vec![debug_notice(
                "debug_breakpoint_not_found",
                &format!("No breakpoint with id '{}' is registered.", request.id),
            )]
        };

        proto::DebugBreakpointRemoveResult {
            result: Some(proto::debug_breakpoint_remove_result::Result::Success(
                proto::DebugBreakpointRemoveResponse {
                    removed,
                    id: request.id,
                    status: Some(stub_debug_status(&state.breakpoints)),
                    notices,
                },
            )),
        }
    }

    pub fn get_debug_events(
        &self,
        request: proto::DebugEventStreamRequest,
    ) -> proto::DebugEventStreamResult {
        proto::DebugEventStreamResult {
            result: Some(proto::debug_event_stream_result::Result::Success(
                proto::DebugEventStreamResponse {
                    events: Vec::new(),
                    from_sequence: request.from_sequence,
                    next_sequence: request.from_sequence,
                    oldest_retained_sequence: 0,
                    newest_sequence: 0,
                    retention_limit: 0,
                    expired: false,
                    overflow: false,
                    notices: vec![debug_notice(
                        "debug_event_stream_unavailable",
                        "Debugger event replay is not supported in the stub debug bridge.",
                    )],
                },
            )),
        }
    }

    pub fn execute_console_command(
        &self,
        request: proto::ConsoleCommandRequest,
    ) -> proto::ConsoleCommandResult {
        let command = request.command.trim().to_string();
        if command.is_empty() {
            return proto::ConsoleCommandResult {
                result: Some(proto::console_command_result::Result::Error(error(
                    proto::BridgeErrorCode::InvalidAction,
                    "dev console requires a command name.",
                    &[detail(
                        "command",
                        "",
                        "Provide the first positional token after 'dev console'.",
                    )],
                ))),
            };
        }

        let line = if request.line.trim().is_empty() {
            canonical_console_line(&command, &request.args)
        } else {
            request.line.trim().to_string()
        };
        let output = mock_console_output(&command, &request.args, &line);
        proto::ConsoleCommandResult {
            result: Some(proto::console_command_result::Result::Success(
                proto::ConsoleCommandResponse {
                    request_id: request.request_id,
                    command,
                    args: request.args,
                    line,
                    accepted: true,
                    success: output.success,
                    output: output.text.clone(),
                    output_lines: output_lines(&output.text),
                    source: proto::DataSource::Stub as i32,
                    provisional: true,
                    notices: Vec::new(),
                },
            )),
        }
    }

}
