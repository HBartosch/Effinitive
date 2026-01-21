namespace EffinitiveFramework.Core.Http2;

/// <summary>
/// HTTP/2 protocol constants and magic values
/// </summary>
public static class Http2Constants
{
    // Connection preface sent by client
    public static ReadOnlySpan<byte> ClientPreface => "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8;
    
    // Frame types
    public const byte FrameTypeData = 0x00;
    public const byte FrameTypeHeaders = 0x01;
    public const byte FrameTypePriority = 0x02;
    public const byte FrameTypeRstStream = 0x03;
    public const byte FrameTypeSettings = 0x04;
    public const byte FrameTypePushPromise = 0x05;
    public const byte FrameTypePing = 0x06;
    public const byte FrameTypeGoAway = 0x07;
    public const byte FrameTypeWindowUpdate = 0x08;
    public const byte FrameTypeContinuation = 0x09;
    
    // Frame flags
    public const byte FlagEndStream = 0x01;
    public const byte FlagEndHeaders = 0x04;
    public const byte FlagPadded = 0x08;
    public const byte FlagPriority = 0x20;
    public const byte FlagAck = 0x01; // For SETTINGS and PING
    
    // Settings parameters
    public const ushort SettingsHeaderTableSize = 0x01;
    public const ushort SettingsEnablePush = 0x02;
    public const ushort SettingsMaxConcurrentStreams = 0x03;
    public const ushort SettingsInitialWindowSize = 0x04;
    public const ushort SettingsMaxFrameSize = 0x05;
    public const ushort SettingsMaxHeaderListSize = 0x06;
    
    // Default settings values
    public const uint DefaultHeaderTableSize = 4096;
    public const uint DefaultEnablePush = 1; // RFC 7540 §6.5.2: Server push enabled by default (client can disable via SETTINGS)
    public const uint DefaultMaxConcurrentStreams = 100;
    public const int DefaultMaxPushedStreams = 10;
    public const int DefaultMaxPushedResourceSize = 1024 * 1024; // 1MB
    public const uint DefaultInitialWindowSize = 65535;
    public const uint DefaultMaxFrameSize = 16384;
    public const uint DefaultMaxHeaderListSize = 8192;
    
    // Error codes
    public const uint ErrorNoError = 0x00;
    public const uint ErrorProtocolError = 0x01;
    public const uint ErrorInternalError = 0x02;
    public const uint ErrorFlowControlError = 0x03;
    public const uint ErrorSettingsTimeout = 0x04;
    public const uint ErrorStreamClosed = 0x05;
    public const uint ErrorFrameSizeError = 0x06;
    public const uint ErrorRefusedStream = 0x07;
    public const uint ErrorCancel = 0x08;
    public const uint ErrorCompressionError = 0x09;
    public const uint ErrorConnectError = 0x0a;
    public const uint ErrorEnhanceYourCalm = 0x0b;
    public const uint ErrorInadequateSecurity = 0x0c;
    public const uint ErrorHttp11Required = 0x0d;
    
    // Frame header size
    public const int FrameHeaderLength = 9;
    
    // Stream ID masks
    public const int StreamIdMask = 0x7FFFFFFF;
    
    // Connection stream ID (0)
    public const int ConnectionStreamId = 0;
}
