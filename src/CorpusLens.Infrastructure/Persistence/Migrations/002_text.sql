BEGIN CTX_DDL.CREATE_PREFERENCE('corpus_world_lexer', 'WORLD_LEXER'); END;
--#
CREATE INDEX idx_search_chunk_text ON search_chunk(raw_text) INDEXTYPE IS CTXSYS.CONTEXT PARAMETERS ('LEXER corpus_world_lexer SYNC (ON COMMIT)')
