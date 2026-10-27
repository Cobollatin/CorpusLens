CREATE TABLE corpus_source (id VARCHAR2(128) PRIMARY KEY, name NVARCHAR2(500) NOT NULL, language VARCHAR2(16) NOT NULL, edition NVARCHAR2(500), rights_assessment CLOB)
--#
CREATE TABLE source_revision (id VARCHAR2(128) PRIMARY KEY, source_id VARCHAR2(128) NOT NULL REFERENCES corpus_source(id), artifact_sha256 CHAR(64) NOT NULL, parser_version VARCHAR2(100) NOT NULL, normalization_version VARCHAR2(100) NOT NULL, status VARCHAR2(16) DEFAULT 'Staging' NOT NULL CHECK (status IN ('Staging','Audited','Indexing','Ready','Active','Quarantined','Superseded')), created_at TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL)
--#
CREATE UNIQUE INDEX uq_source_active ON source_revision (CASE WHEN status = 'Active' THEN source_id END)
--#
CREATE TABLE document (id VARCHAR2(128) PRIMARY KEY, revision_id VARCHAR2(128) NOT NULL REFERENCES source_revision(id), native_id NVARCHAR2(500) NOT NULL, title NVARCHAR2(1000), language VARCHAR2(16) NOT NULL)
--#
CREATE TABLE document_node (id VARCHAR2(128) PRIMARY KEY, document_id VARCHAR2(128) NOT NULL REFERENCES document(id), parent_id VARCHAR2(128) REFERENCES document_node(id), kind VARCHAR2(64) NOT NULL, native_id NVARCHAR2(500) NOT NULL, attributes_json CLOB CHECK (attributes_json IS JSON))
--#
CREATE TABLE text_span (id VARCHAR2(128) PRIMARY KEY, node_id VARCHAR2(128) NOT NULL REFERENCES document_node(id), raw_text CLOB NOT NULL, sha256 CHAR(64) NOT NULL, source_locator NVARCHAR2(2000))
--#
CREATE TABLE search_chunk (id VARCHAR2(128) PRIMARY KEY, revision_id VARCHAR2(128) NOT NULL REFERENCES source_revision(id), layer VARCHAR2(64) NOT NULL, raw_text CLOB NOT NULL, embedding_input CLOB NOT NULL, input_sha256 CHAR(64) NOT NULL, context_header NVARCHAR2(2000))
--#
CREATE TABLE chunk_segment (chunk_id VARCHAR2(128) NOT NULL REFERENCES search_chunk(id), text_span_id VARCHAR2(128) NOT NULL REFERENCES text_span(id), start_offset NUMBER(10) NOT NULL, end_offset NUMBER(10) NOT NULL, CONSTRAINT pk_chunk_segment PRIMARY KEY(chunk_id,text_span_id,start_offset), CONSTRAINT ck_chunk_segment_range CHECK (start_offset >= 0 AND end_offset > start_offset))
--#
CREATE TABLE embedding_model (id VARCHAR2(128) PRIMARY KEY, provider VARCHAR2(100) NOT NULL, name VARCHAR2(200) NOT NULL, dimensions NUMBER(5) NOT NULL CHECK (dimensions = 1024), normalization_version VARCHAR2(100) NOT NULL)
--#
CREATE TABLE chunk_embedding (chunk_id VARCHAR2(128) NOT NULL REFERENCES search_chunk(id), model_id VARCHAR2(128) NOT NULL REFERENCES embedding_model(id), embedding VECTOR(1024, FLOAT32) NOT NULL, token_count NUMBER(10) NOT NULL, CONSTRAINT pk_chunk_embedding PRIMARY KEY(chunk_id,model_id))
--#
CREATE TABLE ingest_job (id VARCHAR2(128) PRIMARY KEY, revision_id VARCHAR2(128) NOT NULL REFERENCES source_revision(id), state VARCHAR2(32) NOT NULL, created_at TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL, external_batch_id VARCHAR2(128), input_file_id VARCHAR2(128), output_file_id VARCHAR2(128), estimated_tokens NUMBER(18), estimated_cost_usd NUMBER(12,6), last_error NVARCHAR2(2000))
--#
CREATE TABLE coverage_audit (revision_id VARCHAR2(128) PRIMARY KEY REFERENCES source_revision(id), passed NUMBER(1) NOT NULL CHECK (passed IN (0,1)), report_json CLOB NOT NULL CHECK (report_json IS JSON), audited_at TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL)
--#
CREATE TABLE query_embedding_cache (cache_key CHAR(64) PRIMARY KEY, model_id VARCHAR2(128) NOT NULL REFERENCES embedding_model(id), language VARCHAR2(16) NOT NULL, embedding VECTOR(1024, FLOAT32) NOT NULL, expires_at TIMESTAMP WITH TIME ZONE NOT NULL)
--#
INSERT INTO embedding_model(id,provider,name,dimensions,normalization_version) VALUES('text-embedding-3-large-1024','OpenAI','text-embedding-3-large',1024,'unicode-whitespace-case-v1')
