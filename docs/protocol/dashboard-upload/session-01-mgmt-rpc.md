### Session 0x01 management RPC envelope

Management RPCs use `0xFF`-prefixed envelope:

```
FF(1)  inner_len(4 LE)  token(4 LE)  data(inner_len)  CRC32(4)
```

Token links requests to responses. Multi-chunk messages also have per-chunk CRC trailers. Message at t=5.2s in capture carries zlib-compressed device log (7163 bytes, UTF-16BE) listing installed dashboards and rendering status.
