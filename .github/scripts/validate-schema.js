// Validates a manifest against the N.I.N.A. manifest repository's schema, the same way its
// validate-latest-manifest.js does, without downloading the installer. Needs ajv and ajv-formats,
// which `npm install` in a checkout of that repository provides (point NODE_PATH at its node_modules).
//
//     node validate-schema.js <manifest.schema.json> <manifest.json>
const fs = require('fs');
const Ajv = require('ajv');
const addFormats = require('ajv-formats');

const [schemaPath, manifestPath] = process.argv.slice(2);
if (!schemaPath || !manifestPath) {
    console.error('Usage: node validate-schema.js <manifest.schema.json> <manifest.json>');
    process.exit(2);
}

const ajv = new Ajv();
addFormats(ajv);
const validate = ajv.compile(JSON.parse(fs.readFileSync(schemaPath, 'utf8')));
const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8').replace(/^﻿/, ''));

if (!validate(manifest)) {
    console.error(`${manifestPath} does not match the schema:`);
    console.error(JSON.stringify(validate.errors, null, 2));
    process.exit(1);
}
console.log(`${manifestPath} matches the N.I.N.A. manifest schema.`);
