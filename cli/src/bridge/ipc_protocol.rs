use prost::Message;
use std::io::{self, Read, Write};

pub const MAGIC: u32 = 0x4c545053;
pub const VERSION: u16 = 0;
const HEADER_LEN: usize = 12;
const MAX_PAYLOAD_BYTES: usize = 64 * 1024 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u16)]
pub enum Method {
    Handshake = 1,
    GetModels = 44,
    GetCombatPreview = 53,
    GetMapDrawings = 54,
    UnhoverRuntimeSceneControl = 55,
    // 2 (legacy GetState) and 45 (legacy WatchState) are retired and must not be reused.
    GetState = 46,
    WatchState = 51,
    WatchCombatEvents = 52,
    InspectPresentationResourceScenes = 47,
    InspectPresentationLocalization = 48,
    GetRuntimeTransitionStatus = 50,
    ExecuteAction = 3,
    LoadFixture = 4,
    GetLogs = 5,
    GetDebugStatus = 6,
    StartDebugSession = 17,
    GetDebugSessionStatus = 18,
    EndDebugSession = 19,
    PauseDebug = 7,
    ResumeDebug = 8,
    StepDebug = 9,
    WaitDebug = 20,
    ListBreakpoints = 10,
    AddBreakpoint = 11,
    RemoveBreakpoint = 12,
    GetRuntimeSceneTree = 13,
    GetRuntimeSceneNode = 14,
    GetScreenshot = 15,
    ExtractAsset = 16,
    GetAssetCatalog = 38,
    HoverRuntimeSceneControl = 39,
    CloseGame = 21,
    CaptureScenario = 22,
    RestoreScenario = 23,
    CaptureCheckpoint = 24,
    RestoreCheckpoint = 25,
    ListCheckpoints = 26,
    DeleteCheckpoint = 27,
    RecordFixture = 28,
    GetHotReloadStatus = 29,
    RequestHotReload = 30,
    ExplainAsset = 31,
    ExecuteConsoleCommand = 32,
    GetDebugEvents = 33,
    SetRuntimeSceneNodeVisible = 34,
    GetMods = 40,
    GetReference = 49,
}

impl Method {
    pub fn name(self) -> &'static str {
        match self {
            Self::Handshake => "game.info",
            Self::GetState => "state",
            Self::WatchState => "state.watch",
            Self::WatchCombatEvents => "events.watch",
            Self::GetModels => "models",
            Self::GetCombatPreview => "combat.preview",
            Self::GetMapDrawings => "map.drawings",
            Self::ExecuteAction => "act",
            Self::LoadFixture => "fixture.load",
            Self::GetLogs => "logs",
            Self::GetDebugStatus => "debug.status",
            Self::StartDebugSession => "debug.session.start",
            Self::GetDebugSessionStatus => "debug.session.status",
            Self::EndDebugSession => "debug.session.end",
            Self::PauseDebug => "debug.pause",
            Self::ResumeDebug => "debug.resume",
            Self::StepDebug => "debug.step",
            Self::WaitDebug => "debug.wait",
            Self::ListBreakpoints => "breakpoint.list",
            Self::AddBreakpoint => "breakpoint.add",
            Self::RemoveBreakpoint => "breakpoint.remove",
            Self::GetRuntimeSceneTree => "scene-tree",
            Self::GetRuntimeSceneNode => "scene-node",
            Self::SetRuntimeSceneNodeVisible => "scene-node.set-visible",
            Self::HoverRuntimeSceneControl => "scene-control.hover",
            Self::UnhoverRuntimeSceneControl => "scene-control.unhover",
            Self::GetRuntimeTransitionStatus => "runtime-transition-status",
            Self::InspectPresentationResourceScenes => "presentation.resource-scenes",
            Self::InspectPresentationLocalization => "presentation.localization",
            Self::GetScreenshot => "screenshot",
            Self::ExtractAsset => "asset.extract",
            Self::GetAssetCatalog => "asset.catalog",
            Self::CloseGame => "game.close",
            Self::CaptureScenario => "scenario.capture",
            Self::RestoreScenario => "scenario.restore",
            Self::CaptureCheckpoint => "checkpoint.capture",
            Self::RestoreCheckpoint => "checkpoint.restore",
            Self::ListCheckpoints => "checkpoint.list",
            Self::DeleteCheckpoint => "checkpoint.delete",
            Self::RecordFixture => "fixture.record",
            Self::GetHotReloadStatus => "hot-reload.status",
            Self::RequestHotReload => "hot-reload.request",
            Self::ExplainAsset => "asset.explain",
            Self::ExecuteConsoleCommand => "console",
            Self::GetDebugEvents => "debug.events",
            Self::GetMods => "game.mods.active",
            Self::GetReference => "reference",
        }
    }

    pub fn from_tag(tag: u16) -> Option<Self> {
        match tag {
            1 => Some(Self::Handshake),
            46 => Some(Self::GetState),
            44 => Some(Self::GetModels),
            53 => Some(Self::GetCombatPreview),
            54 => Some(Self::GetMapDrawings),
            51 => Some(Self::WatchState),
            52 => Some(Self::WatchCombatEvents),
            47 => Some(Self::InspectPresentationResourceScenes),
            48 => Some(Self::InspectPresentationLocalization),
            50 => Some(Self::GetRuntimeTransitionStatus),
            3 => Some(Self::ExecuteAction),
            4 => Some(Self::LoadFixture),
            5 => Some(Self::GetLogs),
            6 => Some(Self::GetDebugStatus),
            17 => Some(Self::StartDebugSession),
            18 => Some(Self::GetDebugSessionStatus),
            19 => Some(Self::EndDebugSession),
            7 => Some(Self::PauseDebug),
            8 => Some(Self::ResumeDebug),
            9 => Some(Self::StepDebug),
            20 => Some(Self::WaitDebug),
            10 => Some(Self::ListBreakpoints),
            11 => Some(Self::AddBreakpoint),
            12 => Some(Self::RemoveBreakpoint),
            13 => Some(Self::GetRuntimeSceneTree),
            14 => Some(Self::GetRuntimeSceneNode),
            34 => Some(Self::SetRuntimeSceneNodeVisible),
            15 => Some(Self::GetScreenshot),
            16 => Some(Self::ExtractAsset),
            38 => Some(Self::GetAssetCatalog),
            39 => Some(Self::HoverRuntimeSceneControl),
            55 => Some(Self::UnhoverRuntimeSceneControl),
            21 => Some(Self::CloseGame),
            22 => Some(Self::CaptureScenario),
            23 => Some(Self::RestoreScenario),
            24 => Some(Self::CaptureCheckpoint),
            25 => Some(Self::RestoreCheckpoint),
            26 => Some(Self::ListCheckpoints),
            27 => Some(Self::DeleteCheckpoint),
            28 => Some(Self::RecordFixture),
            29 => Some(Self::GetHotReloadStatus),
            30 => Some(Self::RequestHotReload),
            31 => Some(Self::ExplainAsset),
            32 => Some(Self::ExecuteConsoleCommand),
            33 => Some(Self::GetDebugEvents),
            40 => Some(Self::GetMods),
            49 => Some(Self::GetReference),
            _ => None,
        }
    }

    pub fn tag(self) -> u16 {
        self as u16
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u16)]
pub enum ResponseStatus {
    Success = 0,
    ProtocolError = 1,
}

impl ResponseStatus {
    pub fn from_tag(tag: u16) -> Option<Self> {
        match tag {
            0 => Some(Self::Success),
            1 => Some(Self::ProtocolError),
            _ => None,
        }
    }

    pub fn tag(self) -> u16 {
        self as u16
    }
}

#[derive(Debug)]
pub struct RequestFrame {
    pub method: Method,
    pub payload: Vec<u8>,
}

#[derive(Debug)]
pub struct ResponseFrame {
    pub status: ResponseStatus,
    pub payload: Vec<u8>,
}

pub fn write_request_frame<W, M>(writer: &mut W, method: Method, message: &M) -> io::Result<()>
where
    W: Write,
    M: Message,
{
    write_frame(writer, method.tag(), &message.encode_to_vec())
}

pub fn read_request_frame<R>(reader: &mut R) -> io::Result<RequestFrame>
where
    R: Read,
{
    let (tag, payload) = read_frame(reader)?;
    let method = Method::from_tag(tag).ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            format!("unsupported bridge IPC method tag '{tag}'"),
        )
    })?;
    Ok(RequestFrame { method, payload })
}

pub fn write_response_frame<W>(
    writer: &mut W,
    status: ResponseStatus,
    payload: &[u8],
) -> io::Result<()>
where
    W: Write,
{
    write_frame(writer, status.tag(), payload)
}

pub fn write_message_response<W, M>(writer: &mut W, message: &M) -> io::Result<()>
where
    W: Write,
    M: Message,
{
    write_response_frame(writer, ResponseStatus::Success, &message.encode_to_vec())
}

pub fn read_response_frame<R>(reader: &mut R) -> io::Result<ResponseFrame>
where
    R: Read,
{
    let (tag, payload) = read_frame(reader)?;
    let status = ResponseStatus::from_tag(tag).ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            format!("unsupported bridge IPC response status '{tag}'"),
        )
    })?;
    Ok(ResponseFrame { status, payload })
}

fn read_frame<R>(reader: &mut R) -> io::Result<(u16, Vec<u8>)>
where
    R: Read,
{
    let mut header = [0u8; HEADER_LEN];
    reader.read_exact(&mut header)?;

    let magic = u32::from_le_bytes(header[0..4].try_into().expect("magic bytes"));
    if magic != MAGIC {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "bridge IPC frame did not start with the expected magic header",
        ));
    }

    let version = u16::from_le_bytes(header[4..6].try_into().expect("version bytes"));
    if version != VERSION {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("bridge IPC frame version '{version}' is not supported"),
        ));
    }

    let tag = u16::from_le_bytes(header[6..8].try_into().expect("tag bytes"));
    let payload_len = u32::from_le_bytes(header[8..12].try_into().expect("length bytes"));
    if payload_len as usize > MAX_PAYLOAD_BYTES {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!(
                "bridge IPC payload length '{}' exceeds the supported limit",
                payload_len
            ),
        ));
    }

    let mut payload = vec![0u8; payload_len as usize];
    if payload_len > 0 {
        reader.read_exact(&mut payload)?;
    }

    Ok((tag, payload))
}

fn write_frame<W>(writer: &mut W, tag: u16, payload: &[u8]) -> io::Result<()>
where
    W: Write,
{
    if payload.len() > MAX_PAYLOAD_BYTES {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!(
                "bridge IPC payload length '{}' exceeds the supported limit",
                payload.len()
            ),
        ));
    }

    writer.write_all(&MAGIC.to_le_bytes())?;
    writer.write_all(&VERSION.to_le_bytes())?;
    writer.write_all(&tag.to_le_bytes())?;
    writer.write_all(&(payload.len() as u32).to_le_bytes())?;
    if !payload.is_empty() {
        writer.write_all(payload)?;
    }
    writer.flush()
}
