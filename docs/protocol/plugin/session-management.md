### Session management

Plugin opens sessions 0x01 (management), 0x02 (telemetry = `FlagByte`), 0x03 (aux config) directly with type=0x81 frames. 0x01 and 0x02 wait up to 500ms each for fc:00 ack. 0x03 opened fire-and-forget for doc compliance — plugin never writes on 0x03, but any unsolicited device data on 0x03 is ACKed to avoid wheel-retransmit stalls.

Device-initiated sessions (0x04/0x06/0x08/0x09/0x0a) accepted via `OnMessageDuringPreamble` handling type=0x81 frames: plugin echoes host's `open_seq` (payload bytes 6-7) in `fc:00` ACK with same session byte. Handler stays subscribed for entire active connection so session 0x04 post-upload directory refresh, session 0x09 configJson state updates, and session 0x0a RPC replies keep flowing beyond ~1s preamble window.
