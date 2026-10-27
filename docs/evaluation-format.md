# Bilingual evaluation format

Store gold queries as UTF-8 JSON Lines. Each line has `id`, `language` (`en` or `es`), `query`, `category` (`paraphrase`, `cross-language`, `multi-verse`, `citation`, or `negative`), `expectedVerseIds` (BBCCCVVV strings), and optional `notes`. Do not include source text or private identifiers in shared fixtures.

Example: `{"id":"citation-john-3-16-en","language":"en","query":"John 3:16","category":"citation","expectedVerseIds":["43003016"]}`
