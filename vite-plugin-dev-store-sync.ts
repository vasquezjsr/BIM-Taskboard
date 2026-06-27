import fs from 'node:fs';
import path from 'node:path';
import type { Plugin } from 'vite';

const SYNC_ROUTE = '/__dev/store-sync';

export function devStoreSyncPlugin(): Plugin {
  const syncFile = path.resolve(process.cwd(), '.dev-store-sync.json');

  return {
    name: 'dev-store-sync',
    configureServer(server) {
      server.middlewares.use(SYNC_ROUTE, (req, res, next) => {
        res.setHeader('Access-Control-Allow-Origin', '*');
        res.setHeader('Access-Control-Allow-Methods', 'GET, PUT, DELETE, OPTIONS');
        res.setHeader('Access-Control-Allow-Headers', 'Content-Type');

        if (req.method === 'OPTIONS') {
          res.statusCode = 204;
          res.end();
          return;
        }

        if (req.method === 'GET') {
          if (!fs.existsSync(syncFile)) {
            res.statusCode = 404;
            res.setHeader('Content-Type', 'application/json');
            res.end('{}');
            return;
          }
          res.setHeader('Content-Type', 'application/json');
          res.end(fs.readFileSync(syncFile, 'utf8'));
          return;
        }

        if (req.method === 'PUT') {
          let body = '';
          req.on('data', (chunk) => {
            body += chunk;
          });
          req.on('end', () => {
            fs.writeFileSync(syncFile, body, 'utf8');
            res.statusCode = 200;
            res.end('ok');
          });
          return;
        }

        if (req.method === 'DELETE') {
          if (fs.existsSync(syncFile)) fs.unlinkSync(syncFile);
          res.statusCode = 204;
          res.end();
          return;
        }

        next();
      });
    },
  };
}
