export interface WireImage {
  mimeType: string;
  /** Base64 without the data: prefix. */
  data: string;
}

const MAX_EDGE = 1024;

/** Reference frames can be many megabytes; the helper only needs enough detail to read the scene. */
export async function toWireImage(file: Blob): Promise<WireImage> {
  const bitmap = await createImageBitmap(file);
  const scale = Math.min(1, MAX_EDGE / Math.max(bitmap.width, bitmap.height));
  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.round(bitmap.width * scale));
  canvas.height = Math.max(1, Math.round(bitmap.height * scale));
  canvas.getContext('2d')?.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();
  const jpeg = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.85));
  if (!jpeg) throw new Error('The image could not be prepared for the helper.');
  const bytes = new Uint8Array(await jpeg.arrayBuffer());
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 0x8000) binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  return { mimeType: 'image/jpeg', data: btoa(binary) };
}
