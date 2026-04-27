### Type 0x81 — session channel open payload

Device sends type `0x81` to initiate or acknowledge session. Payload 4 bytes:

```
session_id(2 LE)  receive_window(2 LE)
```

Observed: `04 00 fd 02` → session 4, window 765.
