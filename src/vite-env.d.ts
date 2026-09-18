/// <reference types="vite/client" />

declare module '*.png?inline' {
  const source: string;
  export default source;
}
