import { useEffect, useRef, useState, type DragEvent } from 'react';
import { imageRequirementLabel } from './scenes';

export interface Scene {
  key: number;
  prompt: string;
  file: File | null;
}

interface Props {
  scene: Scene;
  index: number;
  onlyScene: boolean;
  onChange: (patch: Partial<Scene>) => void;
  onRemove: () => void;
  onDropError: (message: string) => void;
}

export function SceneEditor({ scene, index, onlyScene, onChange, onRemove, onDropError }: Props) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [dragOver, setDragOver] = useState(false);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const first = index === 0;
  const prefix = `scene-${scene.key}`;

  useEffect(() => {
    if (!scene.file) {
      setPreviewUrl(null);
      return undefined;
    }
    const url = URL.createObjectURL(scene.file);
    setPreviewUrl(url);
    return () => URL.revokeObjectURL(url);
  }, [scene.file]);

  const onDrop = (event: DragEvent<HTMLLabelElement>) => {
    event.preventDefault();
    setDragOver(false);
    const input = inputRef.current;
    if (event.dataTransfer.files.length !== 1) {
      onDropError('Drop one reference image per scene.');
      return;
    }
    if (input) input.files = event.dataTransfer.files;
    onChange({ file: event.dataTransfer.files[0] ?? null });
  };

  const removeImage = () => {
    if (inputRef.current) {
      inputRef.current.value = '';
      inputRef.current.focus();
    }
    onChange({ file: null });
  };

  return (
    <li className="scene">
      <div className="scene-header" hidden={onlyScene}>
        <h3 className="scene-title">Scene {index + 1}</h3>
        <button type="button" className="text-button remove-scene" hidden={first} onClick={onRemove}>Remove scene</button>
      </div>
      <div className="field">
        <span className="field-label">
          Reference image <span className="required-label image-requirement">{imageRequirementLabel(index)}</span>
        </span>
        <label
          className={`upload-area${dragOver ? ' dragover' : ''}`}
          htmlFor={`${prefix}-image`}
          onDragEnter={(event) => { event.preventDefault(); setDragOver(true); }}
          onDragOver={(event) => { event.preventDefault(); setDragOver(true); }}
          onDragLeave={() => setDragOver(false)}
          onDrop={onDrop}
        >
          <input
            ref={inputRef}
            id={`${prefix}-image`}
            type="file"
            accept="image/png,image/jpeg,image/webp"
            required={first}
            onChange={(event) => onChange({ file: event.target.files?.[0] ?? null })}
          />
          <span className="upload-placeholder" hidden={!!scene.file}>
            <strong>Choose an image or drop it here</strong>
            <span>PNG, JPEG or WebP</span>
          </span>
          <img className="image-preview" alt="Selected reference frame" hidden={!scene.file} src={previewUrl ?? undefined} />
        </label>
        <div className="image-caption">
          <span className="image-name">
            {scene.file ? `${scene.file.name} · ${(scene.file.size / 1048576).toFixed(1)} MB` : 'No image selected'}
          </span>
          <button type="button" className="text-button remove-image" hidden={!scene.file} onClick={removeImage}>Remove</button>
        </div>
      </div>
      <div className="field">
        <label className="prompt-label" htmlFor={`${prefix}-prompt`}>Describe the scene</label>
        <textarea
          id={`${prefix}-prompt`}
          rows={4}
          maxLength={4000}
          required
          placeholder="A wide shot of a clean warehouse aisle. A worker wearing a hard hat walks along the marked pedestrian path. The camera moves slowly alongside."
          value={scene.prompt}
          onChange={(event) => onChange({ prompt: event.target.value })}
        />
        <div className="prompt-tools">
          <span className="field-help">Scene, action, then camera movement.</span>
          <span className="prompt-count">{scene.prompt.length} / 4000</span>
        </div>
      </div>
    </li>
  );
}
