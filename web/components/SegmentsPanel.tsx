import React from 'react';
import { Download, X } from 'lucide-react';
import type { ExportMode, ExportStatusResponse, Segment } from '../types';
import { formatTimecode } from '../utils/timecode';

interface SegmentsPanelProps {
  segments: Segment[];
  selectedSegmentId: string | null;
  exportMode: ExportMode;
  crf: number;
  template: string;
  exportStatus: ExportStatusResponse | null;
  isExporting: boolean;
  onSelectSegment: (id: string) => void;
  onToggleSegment: (id: string) => void;
  onRenameSegment: (id: string, name: string) => void;
  onChangeCrf: (crf: number) => void;
  onChangeTemplate: (template: string) => void;
  onCancelExport: () => void;
}

export const SegmentsPanel: React.FC<SegmentsPanelProps> = ({
  segments,
  selectedSegmentId,
  exportMode,
  crf,
  template,
  exportStatus,
  isExporting,
  onSelectSegment,
  onToggleSegment,
  onRenameSegment,
  onChangeCrf,
  onChangeTemplate,
  onCancelExport,
}) => {
  const totalPct = exportStatus ? Math.round(exportStatus.totalFraction * 100) : 0;

  return (
    <div className="grid grid-cols-1 gap-3 border-t border-[#2E3440] bg-[#1A1D23] px-3 py-2.5 lg:grid-cols-12">
      {/* Liste des segments */}
      <div className="flex min-h-[155px] flex-col lg:col-span-6">
        <div className="mb-1.5 flex items-center justify-between text-[11px] font-semibold tracking-wider text-[#9AA3B2]">
          <span>LISTE DES SEGMENTS ({segments.length})</span>
          <span className="font-normal text-[#666E7D]">
            {segments.filter((s) => s.enabled).length} inclus
          </span>
        </div>

        <div className="flex-1 overflow-auto rounded border border-[#2E3440] bg-[#121417]">
          <table className="w-full border-collapse text-left text-xs">
            <thead className="sticky top-0 bg-[#22262E] text-[#9AA3B2]">
              <tr className="border-b border-[#2E3440]">
                <th className="w-8 px-2 py-1.5 font-semibold">#</th>
                <th className="px-2 py-1.5 font-semibold">Nom</th>
                <th className="w-24 px-2 py-1.5 font-semibold">Début</th>
                <th className="w-24 px-2 py-1.5 font-semibold">Fin</th>
                <th className="w-24 px-2 py-1.5 font-semibold">Durée</th>
                <th className="w-14 px-2 py-1.5 text-center font-semibold">Inclus</th>
              </tr>
            </thead>
            <tbody>
              {segments.length === 0 ? (
                <tr>
                  <td colSpan={6} className="px-3 py-8 text-center text-[#666E7D]">
                    Aucun segment. Ouvrez une vidéo ou chargez la vidéo de démonstration.
                  </td>
                </tr>
              ) : (
                segments.map((seg, idx) => {
                  const selected = seg.id === selectedSegmentId;
                  return (
                    <tr
                      key={seg.id}
                      onClick={() => onSelectSegment(seg.id)}
                      className={`cursor-pointer border-b border-[#242832] transition-colors ${
                        selected ? 'bg-[#1E3A5F]/60' : 'hover:bg-[#2A2F3A]/60'
                      } ${seg.enabled ? 'text-[#EDF0F5]' : 'text-[#666E7D]'}`}
                    >
                      <td className="font-mono-tabular px-2 py-1">{idx + 1}</td>
                      <td className="px-2 py-1" onClick={(e) => e.stopPropagation()}>
                        <input
                          type="text"
                          value={seg.name}
                          onChange={(e) => onRenameSegment(seg.id, e.target.value)}
                          onFocus={() => onSelectSegment(seg.id)}
                          className="w-full rounded border border-transparent bg-transparent px-1 py-0.5 text-xs focus:border-[#3B82F6] focus:bg-[#1A1D23] focus:outline-none"
                        />
                      </td>
                      <td className="font-mono-tabular px-2 py-1">{formatTimecode(seg.start, true)}</td>
                      <td className="font-mono-tabular px-2 py-1">{formatTimecode(seg.end, true)}</td>
                      <td className="font-mono-tabular px-2 py-1">{formatTimecode(seg.end - seg.start, true)}</td>
                      <td className="px-2 py-1 text-center" onClick={(e) => e.stopPropagation()}>
                        <input
                          type="checkbox"
                          checked={seg.enabled}
                          onChange={() => onToggleSegment(seg.id)}
                          className="h-3.5 w-3.5 cursor-pointer accent-[#3B82F6]"
                          aria-label={`Inclure ${seg.name}`}
                        />
                      </td>
                    </tr>
                  );
                })
              )}
            </tbody>
          </table>
        </div>
      </div>

      {/* Options d'export */}
      <div className="flex flex-col rounded border border-[#2E3440] bg-[#121417] p-2.5 lg:col-span-3">
        <div className="mb-2 text-[11px] font-semibold tracking-wider text-[#9AA3B2]">
          OPTIONS D'EXPORT
        </div>

        <label className="mb-2 block text-xs text-[#9AA3B2]">
          Modèle de nom
          <input
            type="text"
            value={template}
            onChange={(e) => onChangeTemplate(e.target.value)}
            className="font-mono-tabular mt-1 w-full rounded border border-[#2E3440] bg-[#1A1D23] px-2 py-1 text-xs text-[#EDF0F5] focus:border-[#3B82F6] focus:outline-none"
          />
        </label>

        <div className="mt-1 flex items-center justify-between text-xs text-[#9AA3B2]">
          <span>Mode actif</span>
          <span className="font-medium text-[#EDF0F5]">
            {exportMode === 'fast' ? 'Rapide (sans perte)' : 'Précis (libx264)'}
          </span>
        </div>

        {exportMode === 'precise' && (
          <div className="mt-2">
            <div className="flex items-center justify-between text-xs text-[#9AA3B2]">
              <span>Qualité (CRF)</span>
              <span className="font-mono-tabular text-[#EDF0F5]">{crf}</span>
            </div>
            <input
              type="range"
              min={14}
              max={28}
              value={crf}
              onChange={(e) => onChangeCrf(Number(e.target.value))}
              className="mt-1 w-full cursor-pointer accent-[#3B82F6]"
            />
          </div>
        )}
      </div>

      {/* Progression & résultats */}
      <div className="flex flex-col rounded border border-[#2E3440] bg-[#121417] p-2.5 lg:col-span-3">
        <div className="mb-2 flex items-center justify-between text-[11px] font-semibold tracking-wider text-[#9AA3B2]">
          <span>PROGRESSION</span>
          {exportStatus && <span className="font-mono-tabular text-[#EDF0F5]">{totalPct}%</span>}
        </div>

        <div className="h-2 w-full overflow-hidden rounded-full border border-[#2E3440] bg-[#22262E]">
          <div
            className="h-full bg-[#3B82F6] transition-all duration-150"
            style={{ width: `${totalPct}%` }}
          />
        </div>

        <div className="mt-2 flex-1 space-y-1.5 overflow-auto text-xs">
          {!exportStatus ? (
            <p className="text-[#666E7D]">En attente d'export.</p>
          ) : (
            exportStatus.segments.map((seg) => (
              <div
                key={seg.segmentId}
                className="flex flex-col gap-0.5 rounded border border-[#242832] bg-[#1A1D23] px-2 py-1"
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="truncate text-[#EDF0F5]">{seg.fileName}</span>
                  {seg.status === 'completed' && seg.downloadUrl ? (
                    <a
                      href={seg.downloadUrl}
                      download={seg.fileName}
                      className="inline-flex shrink-0 items-center gap-1 text-[11px] font-medium text-[#60A5FA] hover:underline"
                    >
                      <Download className="h-3 w-3" />
                      Télécharger
                    </a>
                  ) : (
                    <span className="font-mono-tabular shrink-0 text-[11px] text-[#9AA3B2]">
                      {seg.status === 'error'
                        ? 'Erreur'
                        : seg.status === 'cancelled'
                          ? 'Annulé'
                          : `${Math.round(seg.fraction * 100)}%`}
                    </span>
                  )}
                </div>
                {seg.warning && <p className="text-[10px] text-amber-400">{seg.warning}</p>}
                {seg.error && <p className="text-[10px] text-red-400">{seg.error}</p>}
              </div>
            ))
          )}
        </div>

        {isExporting && (
          <button
            type="button"
            onClick={onCancelExport}
            className="mt-2 inline-flex items-center justify-center gap-1.5 self-end rounded border border-[#2E3440] bg-[#22262E] px-2.5 py-1 text-xs text-[#EDF0F5] hover:bg-[#2A2F3A]"
          >
            <X className="h-3.5 w-3.5" />
            Annuler l'export
          </button>
        )}
      </div>
    </div>
  );
};
