import { useEffect, useState } from 'react';
import { api } from './client';

export interface Features {
  datasetBuilder: boolean;
}

/** Feature gates from the server. Everything stays off until the server says otherwise. */
export function useFeatures(): Features {
  const [features, setFeatures] = useState<Features>({ datasetBuilder: false });

  useEffect(() => {
    let alive = true;
    api
      .get<Partial<Features>>('/api/features')
      .then((value) => alive && setFeatures({ datasetBuilder: value.datasetBuilder === true }))
      .catch(() => undefined);
    return () => {
      alive = false;
    };
  }, []);

  return features;
}
