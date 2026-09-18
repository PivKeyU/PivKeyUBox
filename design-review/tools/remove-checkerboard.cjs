const fs = require('fs');
const path = require('path');
const { PNG } = require('C:\\Users\\PivKeyU\\.cache\\codex-runtimes\\codex-primary-runtime\\dependencies\\node\\node_modules\\pngjs');

const inputDir = path.resolve(__dirname, '..', 'assets', 'poses');

function isBackgroundLike(r, g, b) {
  const high = Math.max(r, g, b);
  const low = Math.min(r, g, b);
  return high - low <= 5 && low >= 242;
}

function removeCheckerboard(filePath) {
  const source = PNG.sync.read(fs.readFileSync(filePath));
  const { width, height } = source;
  const total = width * height;
  const reachable = new Uint8Array(total);
  const queued = new Uint8Array(total);
  const queue = new Int32Array(total);
  let head = 0;
  let tail = 0;

  const enqueue = (x, y) => {
    if (x < 0 || x >= width || y < 0 || y >= height) return;
    const index = y * width + x;
    if (queued[index]) return;
    const offset = index * 4;
    if (!isBackgroundLike(source.data[offset], source.data[offset + 1], source.data[offset + 2])) return;
    queued[index] = 1;
    queue[tail] = index;
    tail += 1;
  };

  for (let x = 0; x < width; x += 1) {
    enqueue(x, 0);
    enqueue(x, height - 1);
  }
  for (let y = 1; y < height - 1; y += 1) {
    enqueue(0, y);
    enqueue(width - 1, y);
  }

  while (head < tail) {
    const index = queue[head];
    head += 1;
    reachable[index] = 1;
    const x = index % width;
    const y = Math.floor(index / width);
    enqueue(x - 1, y);
    enqueue(x + 1, y);
    enqueue(x, y - 1);
    enqueue(x, y + 1);
  }

  for (let index = 0; index < total; index += 1) {
    const offset = index * 4;
    source.data[offset + 3] = reachable[index] ? 0 : 255;
  }

  const temporaryPath = `${filePath}.tmp`;
  fs.writeFileSync(temporaryPath, PNG.sync.write(source));
  fs.renameSync(temporaryPath, filePath);
  console.log(`${path.basename(filePath)}: RGBA, cleared ${tail} edge-connected background pixels`);
}

for (const fileName of fs.readdirSync(inputDir).filter((name) => name.endsWith('.png')).sort()) {
  removeCheckerboard(path.join(inputDir, fileName));
}
