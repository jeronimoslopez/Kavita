# Text sidecar contract

The text sidecar tokenizes Japanese EPUB text for Kavita's dictionary popup. It isn't in this repo. It's built from the shimen repo as that repo's `sidecar` Docker target, so it runs shimen's own tokenizer, word rules and lexicon. These files are what Kavita and the sidecar agree on for `POST /tokenize`, and `GET /health` answers `{"version": "..."}`.

`tokenize-request.schema.json` and `tokenize-response.schema.json` are the request and response. Each token is the array `[s, e, w, r, sub]`, where `s` and `e` are UTF-16 offsets into the block's text, `w` indexes `words` or is null, `r` is the occurrence's hiragana reading or null, and `sub` is null or the compound's parts as `[s, e, w, r]`.

`sample-request.json` and `sample-response.json` are hand-written for now. Kavita's tests deserialize the response, so it has to stay valid against the schema. At deploy time it's replaced by what the real sidecar returns for the sample request.
