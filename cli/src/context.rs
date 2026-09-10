use super::*;

#[derive(Debug, Clone, Copy)]
pub(crate) struct AppContext<'a> {
    pub(crate) config: &'a AppConfig,
    pub(crate) config_origin: &'a ConfigOrigin,
    pub(crate) config_provenance: &'a ConfigProvenance,
    pub(crate) json_output: bool,
    pub(crate) mode: Mode,
    /// Active `--instance` layout, when one was requested. Only the
    /// instance-aware game lifecycle commands consult this; everything else
    /// transparently uses the overlaid `config`.
    pub(crate) instance: Option<&'a crate::instance::InstanceContext>,
}

#[derive(Debug, Clone)]
pub(crate) struct RuntimeContextOwned {
    config: AppConfig,
    config_origin: ConfigOrigin,
    config_provenance: ConfigProvenance,
    mode: Mode,
}

impl RuntimeContextOwned {
    pub(crate) fn from_app_context(context: AppContext<'_>) -> Self {
        Self {
            config: context.config.clone(),
            config_origin: context.config_origin.clone(),
            config_provenance: context.config_provenance.clone(),
            mode: context.mode,
        }
    }

    pub(crate) fn from_app_context_with_config(context: AppContext<'_>, config: AppConfig) -> Self {
        Self {
            config,
            config_origin: context.config_origin.clone(),
            config_provenance: context.config_provenance.clone(),
            mode: context.mode,
        }
    }

    pub(crate) fn as_app_context(&self, json_output: bool) -> AppContext<'_> {
        AppContext {
            config: &self.config,
            config_origin: &self.config_origin,
            config_provenance: &self.config_provenance,
            json_output,
            mode: self.mode,
            // Scoped/owned sub-contexts already carry the overlaid config, so
            // they connect to the right endpoint without re-carrying the
            // instance identity (which only the top-level lifecycle commands
            // need for registry/mirror operations).
            instance: None,
        }
    }
}

pub(crate) fn context_with_transport_rpc_timeout(
    context: AppContext<'_>,
    rpc_timeout_ms: u64,
) -> RuntimeContextOwned {
    let mut config = context.config.clone();
    config.transport.rpc_timeout_ms = (rpc_timeout_ms > 0).then_some(rpc_timeout_ms);
    RuntimeContextOwned::from_app_context_with_config(context, config)
}

pub(crate) fn bridge_client(context: AppContext<'_>) -> RuntimeBridgeClient {
    RuntimeBridgeClient::from_config(&context.config.transport)
}
