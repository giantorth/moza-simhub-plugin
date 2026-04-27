### Chunking (both versions, both directions)

All 7c:00 session data uses SerialStream chunks with CRC-32 trailers (standard ISO 3309). **ALL chunks have CRC-32 trailers, including final chunk** — verified by computing CRC-32 of every chunk's net data across multiple captures. Max 54 net bytes per chunk (58 with CRC).
